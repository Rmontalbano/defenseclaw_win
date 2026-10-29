using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using InfoBar = Wpf.Ui.Controls.InfoBar;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// Makes <c>AutomationProperties.LiveSetting</c> work. Declaring a live region in XAML only sets the UI Automation property; WPF
/// never raises <c>LiveRegionChanged</c> when the bound text of that element changes, so a screen reader was never told, and
/// 34 of the app's 38 declarations (result banners, validation errors, progress lines, the wizard's status text) were silent.
/// <para>
/// This watches every element that declares a live region and raises the event through its automation peer when what it
/// SAYS changes. It hooks the declaration itself (<see cref="Install"/>), which is why no view needs an edit and why it
/// reaches elements that carry an explicit <c>Style</c> and ones inside templates as well as bare ones. What counts as "what it says":
/// </para>
/// <list type="bullet">
/// <item>a <see cref="TextBlock"/>: its text;</item>
/// <item>an <see cref="InfoBar"/>: its title and message while it is open (closing it says nothing; opening it, or changing its message, does);</item>
/// <item>a <see cref="ContentControl"/> holding a string (a card header);</item>
/// <item>any other element (a <c>Border</c> or <c>StackPanel</c> that groups a status line): the text of the <see cref="TextBlock"/>s inside it at the time it loaded.</item>
/// </list>
/// <para>
/// It announces once per change: identical text again is not a change, an empty one is silence, and a change while the
/// element is not on screen (another panel, a window hidden to the tray, a collapsed section) waits until it is. Elements
/// are only watched while loaded, so a panel that is navigated away from costs nothing and a panel that comes back
/// does not re-read what was already there. The evaluation is deferred to the dispatcher's background priority, so a
/// message and the flag that opens its banner, changed one after the other, are one announcement of the finished state.
/// </para>
/// <para>
/// A few elements raise the event themselves from code-behind (the state pill, the palette's status line, the review dialog's
/// tier header and copy confirmation); <see cref="RaisedByCodeProperty"/> tells this to leave them alone rather than say it twice.
/// Tests replace the way it announces through <see cref="UseAnnouncer"/>.
/// </para>
/// </summary>
public static class LiveRegion
{
    /// <summary>Set on an element whose code-behind already raises <c>LiveRegionChanged</c> for it: the behaviour leaves it alone.</summary>
    public static readonly DependencyProperty RaisedByCodeProperty = DependencyProperty.RegisterAttached(
        "RaisedByCode",
        typeof(bool),
        typeof(LiveRegion),
        new PropertyMetadata(false));

    // The state one watched element carries between Loaded and Unloaded.
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(Watch),
        typeof(LiveRegion),
        new PropertyMetadata(null));

    // Mirrors of the watched properties: a binding from the property to one of these is how a change is observed without
    // DependencyPropertyDescriptor (whose handler table keeps the element alive until it is removed by hand).
    private static readonly DependencyProperty MirrorAProperty = Mirror("MirrorA");
    private static readonly DependencyProperty MirrorBProperty = Mirror("MirrorB");
    private static readonly DependencyProperty MirrorCProperty = Mirror("MirrorC");

    // Set once an element has its Loaded / Unloaded handlers, so declaring the setting twice hooks it once.
    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked",
        typeof(bool),
        typeof(LiveRegion),
        new PropertyMetadata(false));

    // A text block inside a container region reports to that container.
    private static readonly DependencyProperty InnerOwnerProperty = DependencyProperty.RegisterAttached(
        "InnerOwner",
        typeof(FrameworkElement),
        typeof(LiveRegion),
        new PropertyMetadata(null));

    private static readonly DependencyProperty InnerMirrorProperty = DependencyProperty.RegisterAttached(
        "InnerMirror",
        typeof(object),
        typeof(LiveRegion),
        new PropertyMetadata(null, OnInnerChanged));

    private static readonly object InstallGate = new();
    private static bool _installed;
    private static Action<FrameworkElement> _announcer = static element => _ = RaiseThroughPeer(element);

    public static bool GetRaisedByCode(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(RaisedByCodeProperty);
    }

    public static void SetRaisedByCode(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(RaisedByCodeProperty, value);
    }

    /// <summary>
    /// Starts watching live regions everywhere in the process, from now on. Idempotent; the app calls it once at startup
    /// (AppearanceService.Initialize), before any element that declares one exists.
    /// <para>
    /// It hooks the declaration itself: the metadata of <c>AutomationProperties.LiveSetting</c> is extended, for the kinds of
    /// element a live region can be on, with a callback that runs when XAML (or code) sets it on an element. That is the only
    /// hook that reaches every declaration - one in a template, one with an explicit style, one on a container - because WPF
    /// broadcasts <c>Loaded</c> only into subtrees that hold an instance handler (a class handler never sees a descendant
    /// load), and an implicit style skips every element that names a style of its own. A metadata override can only be added
    /// before the type has used the property, so this has to run before the first window: which is when it is called.
    /// </para>
    /// </summary>
    public static void Install()
    {
        lock (InstallGate)
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
        }

        foreach (var host in HostTypes)
        {
            try
            {
                AutomationProperties.LiveSettingProperty.OverrideMetadata(host, new FrameworkPropertyMetadata(OnLiveSettingDeclared));
            }
#pragma warning disable CA1031 // A type that already used the property cannot take the hook; the others still can, and the app must start.
            catch (ArgumentException ex)
            {
                Trace.TraceWarning($"Live regions on {host.Name} cannot be watched: {ex.Message}");
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>Replaces what "announce" does until the returned scope is disposed (tests: record instead of raising a UI Automation event).</summary>
    internal static IDisposable UseAnnouncer(Action<FrameworkElement> announcer)
    {
        ArgumentNullException.ThrowIfNull(announcer);
        var previous = _announcer;
        _announcer = announcer;
        return new Restore(() => _announcer = previous);
    }

    /// <summary>
    /// Raises <c>LiveRegionChanged</c> for <paramref name="element"/> when a UI Automation client is listening for it: the
    /// documented pattern (ask whether anyone listens, and only then create the peer), so a machine with no screen reader pays for nothing.
    /// </summary>
    internal static bool RaiseThroughPeer(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return false;
        }

        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        if (peer is null)
        {
            return false;
        }

        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        return true;
    }

    /// <summary>The kinds of element a live region can be declared on; the hook is installed for each (see <see cref="Install"/>).</summary>
    internal static IReadOnlyList<Type> HostTypes { get; } = new[] { typeof(TextBlock), typeof(ContentControl), typeof(Decorator), typeof(Panel) };

    /// <summary>Whether declaring a live region on <paramref name="host"/> reaches this behaviour (false: installed too late for that type).</summary>
    internal static bool IsHooked(Type host) =>
        AutomationProperties.LiveSettingProperty.GetMetadata(host).PropertyChangedCallback?.GetInvocationList()
            .Any(callback => callback.Method.Name == nameof(OnLiveSettingDeclared)) == true;

    /// <summary>How many elements are being watched right now (tests: a panel that unloaded must not leave any behind).</summary>
    internal static int WatchedCount { get; private set; }

    // ------------------------------------------------------------------ attaching

    /// <summary>A live region was declared on <paramref name="sender"/>: watch it from when it loads until it unloads.</summary>
    private static void OnLiveSettingDeclared(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.GetValue(HookedProperty) is true)
        {
            return;
        }

        // Instance handlers, not class handlers: only an instance handler makes WPF broadcast Loaded down to this element.
        element.SetValue(HookedProperty, true);
        element.Loaded += OnLoaded;
        element.Unloaded += OnUnloaded;
        if (element.IsLoaded)
        {
            OnLoaded(element, new RoutedEventArgs());
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element
            && AutomationProperties.GetLiveSetting(element) != AutomationLiveSetting.Off
            && !GetRaisedByCode(element)
            && element.GetValue(StateProperty) is null)
        {
            Attach(element);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.GetValue(StateProperty) is Watch)
        {
            Detach(element);
        }
    }

    private static void Attach(FrameworkElement element)
    {
        var watch = new Watch(element);
        element.SetValue(StateProperty, watch);
        WatchedCount++;

        switch (element)
        {
            case InfoBar:
                Follow(element, InfoBar.IsOpenProperty, MirrorAProperty);
                Follow(element, InfoBar.TitleProperty, MirrorBProperty);
                Follow(element, InfoBar.MessageProperty, MirrorCProperty);
                break;
            case TextBlock:
                Follow(element, TextBlock.TextProperty, MirrorAProperty);
                break;
            case ContentControl:
                Follow(element, ContentControl.ContentProperty, MirrorAProperty);
                break;
            default:
                foreach (var inner in TextBlocksUnder(element))
                {
                    inner.SetValue(InnerOwnerProperty, element);
                    Follow(inner, TextBlock.TextProperty, InnerMirrorProperty);
                    watch.Inner.Add(inner);
                }

                break;
        }

        element.IsVisibleChanged += watch.OnVisibleChanged;

        // What is on screen when the element loads is what was already said: nothing is announced for it.
        watch.Last = SnapshotOf(element);
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not Watch watch)
        {
            return;
        }

        element.IsVisibleChanged -= watch.OnVisibleChanged;
        foreach (var mirror in new[] { MirrorAProperty, MirrorBProperty, MirrorCProperty })
        {
            BindingOperations.ClearBinding(element, mirror);
        }

        foreach (var inner in watch.Inner)
        {
            BindingOperations.ClearBinding(inner, InnerMirrorProperty);
            inner.ClearValue(InnerOwnerProperty);
        }

        watch.Inner.Clear();
        watch.Detached = true;
        element.ClearValue(StateProperty);
        WatchedCount--;
    }

    private static void Follow(DependencyObject source, DependencyProperty watched, DependencyProperty mirror) =>
        BindingOperations.SetBinding(source, mirror, new Binding { Path = new PropertyPath(watched), Source = source, Mode = BindingMode.OneWay });

    private static IEnumerable<TextBlock> TextBlocksUnder(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock text)
            {
                yield return text;
            }

            foreach (var nested in TextBlocksUnder(child))
            {
                yield return nested;
            }
        }
    }

    // ------------------------------------------------------------------ noticing and announcing

    private static DependencyProperty Mirror(string name) => DependencyProperty.RegisterAttached(
        name,
        typeof(object),
        typeof(LiveRegion),
        new PropertyMetadata(null, OnMirrorChanged));

    private static void OnMirrorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender.GetValue(StateProperty) is Watch watch)
        {
            watch.Schedule();
        }
    }

    private static void OnInnerChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender.GetValue(InnerOwnerProperty) is FrameworkElement owner && owner.GetValue(StateProperty) is Watch watch)
        {
            watch.Schedule();
        }
    }

    /// <summary>What an element currently says, for telling one state from the next. Empty means silence.</summary>
    private static string SnapshotOf(FrameworkElement element) => element switch
    {
        InfoBar bar => bar.IsOpen ? string.Join('\n', new[] { bar.Title, bar.Message }.Where(s => !string.IsNullOrWhiteSpace(s))) : string.Empty,
        TextBlock text => text.Text ?? string.Empty,
        ContentControl content => content.Content as string ?? string.Empty,
        _ => element.GetValue(StateProperty) is Watch watch
            ? string.Join('\n', watch.Inner.Select(t => t.Text).Where(s => !string.IsNullOrWhiteSpace(s)))
            : string.Empty,
    };

    private sealed class Watch
    {
        private readonly FrameworkElement _element;
        private bool _pending;

        public Watch(FrameworkElement element)
        {
            _element = element;
        }

        public List<TextBlock> Inner { get; } = new();

        /// <summary>What the element said when it was last announced (or loaded); null after it went quiet.</summary>
        public string? Last { get; set; }

        public bool Detached { get; set; }

        public void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Schedule();

        public void Schedule()
        {
            if (_pending || Detached)
            {
                return;
            }

            _pending = true;
            _ = _element.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Evaluate));
        }

        private void Evaluate()
        {
            _pending = false;
            if (Detached)
            {
                return;
            }

            var now = SnapshotOf(_element);
            if (string.IsNullOrWhiteSpace(now))
            {
                // Gone quiet (an info bar closed, a message cleared), on screen or not: the same words are news again next time.
                Last = null;
                return;
            }

            if (!_element.IsVisible)
            {
                // Not on screen: nothing to say yet. Being shown is a change too, so this runs again then.
                return;
            }

            if (string.Equals(now, Last, StringComparison.Ordinal))
            {
                return;
            }

            Last = now;
            _announcer(_element);
        }
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _restore;

        public Restore(Action restore)
        {
            _restore = restore;
        }

        public void Dispose() => _restore();
    }
}
