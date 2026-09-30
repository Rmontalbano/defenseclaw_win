using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xml.Linq;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Shell;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// Live regions (D4-5). <c>AutomationProperties.LiveSetting</c> only sets a UI Automation property: WPF never raises
/// <c>LiveRegionChanged</c> when the bound text of that element changes, so 34 of the 38 declarations never spoke.
/// <see cref="LiveRegion"/> watches every declaring element as it loads and raises the event through the peer when what it says
/// changes. The announcer is a seam here: these tests record instead of raising a UI Automation event, and assert once per change.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LiveRegionTests
{
    private sealed class Bag : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        private string _message = string.Empty;
        private bool _isOpen;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Text
        {
            get => _text;
            set => Set(ref _text, value, nameof(Text));
        }

        public string Message
        {
            get => _message;
            set => Set(ref _message, value, nameof(Message));
        }

        public bool IsOpen
        {
            get => _isOpen;
            set => Set(ref _isOpen, value, nameof(IsOpen));
        }

        private void Set<T>(ref T field, T value, string name)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>A live region in an off-screen host with a recorder for what it announced.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly IDisposable _scope;
        private readonly OffscreenHost _host;
        private readonly Panel _root = new StackPanel();

        public Scene()
        {
            LiveRegion.Install();
            Announced = new List<string>();
            _scope = LiveRegion.UseAnnouncer(element => Announced.Add(element.Name));
            _host = new OffscreenHost(_root, 400, 300);
        }

        public List<string> Announced { get; }

        public Bag Bag { get; } = new();

        /// <summary>Adds an element, gives it the bag and a name, and lets everything queued run.</summary>
        public T Add<T>(T element, string name, AutomationLiveSetting setting = AutomationLiveSetting.Polite)
            where T : FrameworkElement
        {
            element.Name = name;
            element.DataContext = Bag;
            if (setting != AutomationLiveSetting.Off)
            {
                AutomationProperties.SetLiveSetting(element, setting);
            }

            _root.Children.Add(element);
            Settle();
            return element;
        }

        public void Remove(UIElement element)
        {
            _root.Children.Remove(element);
            Settle();
        }

        public void Settle()
        {
            _host.Relayout();
            UiThread.Settle();
        }

        public void Dispose()
        {
            _scope.Dispose();
            _host.Dispose();
        }
    }

    private static TextBlock BoundText() =>
        WithBinding(new TextBlock(), TextBlock.TextProperty, nameof(Bag.Text));

    private static T WithBinding<T>(T element, DependencyProperty property, string path, BindingMode mode = BindingMode.OneWay)
        where T : FrameworkElement
    {
        _ = element.SetBinding(property, new Binding(path) { Mode = mode });
        return element;
    }

    [Fact]
    public void The_hook_is_installed_for_every_kind_of_element_a_live_region_can_be_declared_on()
    {
        // The hook is a metadata override, which WPF refuses for a type that has already used the property; a failure
        // there is traced and otherwise silent, so this is what would notice it.
        LiveRegion.Install();
        foreach (var host in LiveRegion.HostTypes)
        {
            Assert.True(LiveRegion.IsHooked(host), $"{host.Name} did not take the live-region hook");
        }

        Assert.True(LiveRegion.IsHooked(typeof(InfoBar)));
        Assert.True(LiveRegion.IsHooked(typeof(Border)));
        Assert.True(LiveRegion.IsHooked(typeof(StackPanel)));
        Assert.True(LiveRegion.IsHooked(typeof(Wpf.Ui.Controls.TextBlock)));
    }

    // ------------------------------------------------------------------ a text block

    [Fact]
    public void A_text_block_announces_each_new_text_once_and_not_the_text_it_loaded_with()
    {
        UiThread.Run(() =>
        {
            using var scene = new Scene();
            scene.Bag.Text = "already here";
            _ = scene.Add(BoundText(), "status");
            Assert.Empty(scene.Announced);

            scene.Bag.Text = "Copied";
            scene.Settle();
            Assert.Equal(new[] { "status" }, scene.Announced);

            scene.Bag.Text = "Copied";
            scene.Settle();
            Assert.Single(scene.Announced);

            scene.Bag.Text = "Failed";
            scene.Settle();
            Assert.Equal(2, scene.Announced.Count);
        });
    }

    [Fact]
    public void Text_that_goes_quiet_announces_nothing_and_the_same_words_are_news_again_afterwards()
    {
        UiThread.Run(() =>
        {
            using var scene = new Scene();
            _ = scene.Add(BoundText(), "status");

            scene.Bag.Text = "Saved";
            scene.Settle();
            scene.Bag.Text = string.Empty;
            scene.Settle();
            Assert.Single(scene.Announced);

            scene.Bag.Text = "Saved";
            scene.Settle();
            Assert.Equal(2, scene.Announced.Count);
        });
    }

    [Fact]
    public void Changes_made_one_after_another_are_one_announcement_of_where_they_ended()
    {
        UiThread.Run(() =>
        {
            using var scene = new Scene();
            _ = scene.Add(BoundText(), "status");

            scene.Bag.Text = "first";
            scene.Bag.Text = "second";
            scene.Bag.Text = "third";
            scene.Settle();

            Assert.Single(scene.Announced);
        });
    }

    [Fact]
    public void A_change_while_the_element_is_hidden_waits_until_it_is_shown()
    {
        UiThread.Run(() =>
        {
            using var scene = new Scene();
            var text = scene.Add(BoundText(), "status");

            text.Visibility = Visibility.Collapsed;
            scene.Settle();
            scene.Bag.Text = "Something went wrong";
            scene.Settle();
            Assert.Empty(scene.Announced);

            text.Visibility = Visibility.Visible;
            scene.Settle();
            Assert.Equal(new[] { "status" }, scene.Announced);

            // Hiding and showing it again is not a new message.
            text.Visibility = Visibility.Collapsed;
            scene.Settle();
            text.Visibility = Visibility.Visible;
            scene.Settle();
            Assert.Single(scene.Announced);
        });
    }

    [Fact]
    public void An_element_that_left_the_tree_is_no_longer_watched_and_leaves_nothing_behind()
    {
        UiThread.Run(() =>
        {
            using var scene = new Scene();
            var before = LiveRegion.WatchedCount;
            var text = scene.Add(BoundText(), "status");
            Assert.Equal(before + 1, LiveRegion.WatchedCount);

            scene.Remove(text);
            Assert.Equal(before, LiveRegion.WatchedCount);

            scene.Bag.Text = "after";
            scene.Settle();
            Assert.Empty(scene.Announced);
        });
    }

    [Fact]
    public void An_element_with_no_live_setting_and_one_that_raises_its_own_event_are_left_alone()
    {
        UiThread.Run(() =>
        {
            using var scene = new Scene();
            _ = scene.Add(BoundText(), "plain", AutomationLiveSetting.Off);
            var raised = scene.Add(BoundText(), "raised_in_code");
            // Set before it loads, as the XAML attribute is; here it is set after the fact, so reload it.
            scene.Remove(raised);
            LiveRegion.SetRaisedByCode(raised, true);
            scene.Add(raised, "raised_in_code");

            scene.Bag.Text = "changed";
            scene.Settle();

            Assert.Empty(scene.Announced);
        });
    }

    // ------------------------------------------------------------------ an info bar

    [Fact]
    public void An_info_bar_announces_when_it_opens_and_when_its_message_changes_and_not_when_it_closes()
    {
        UiThread.Run(() =>
        {
            using var scene = new Scene();
            var bar = new InfoBar { Title = "Done", IsClosable = true };
            _ = bar.SetBinding(InfoBar.MessageProperty, new Binding(nameof(Bag.Message)));
            _ = bar.SetBinding(InfoBar.IsOpenProperty, new Binding(nameof(Bag.IsOpen)) { Mode = BindingMode.TwoWay });
            _ = scene.Add(bar, "result");
            Assert.Empty(scene.Announced);

            // The message and the flag that opens the banner change one after the other: one announcement of the finished state.
            scene.Bag.IsOpen = true;
            scene.Bag.Message = "Skill enabled";
            scene.Settle();
            Assert.Equal(new[] { "result" }, scene.Announced);

            scene.Bag.Message = "Skill disabled";
            scene.Settle();
            Assert.Equal(2, scene.Announced.Count);

            scene.Bag.IsOpen = false;
            scene.Settle();
            Assert.Equal(2, scene.Announced.Count);

            // Closed and opened again with the same words: news.
            scene.Bag.IsOpen = true;
            scene.Settle();
            Assert.Equal(3, scene.Announced.Count);
        });
    }

    // ------------------------------------------------------------------ containers and headers

    [Fact]
    public void A_container_announces_when_the_text_inside_it_changes()
    {
        UiThread.Run(() =>
        {
            using var scene = new Scene();
            var badge = new Border { Child = BoundText() };
            _ = scene.Add(badge, "badge");
            Assert.Empty(scene.Announced);

            scene.Bag.Text = "Blocked";
            scene.Settle();
            Assert.Equal(new[] { "badge" }, scene.Announced);

            scene.Bag.Text = "Blocked";
            scene.Settle();
            Assert.Single(scene.Announced);
        });
    }

    [Fact]
    public void A_control_holding_a_string_announces_when_the_string_changes()
    {
        UiThread.Run(() =>
        {
            using var scene = new Scene();
            var header = WithBinding(new ContentControl(), ContentControl.ContentProperty, nameof(Bag.Text));
            _ = scene.Add(header, "header");

            scene.Bag.Text = "Output: skill list";
            scene.Settle();

            Assert.Equal(new[] { "header" }, scene.Announced);
        });
    }

    [Fact]
    public void Raising_the_event_for_real_does_nothing_and_throws_nothing_when_no_client_is_listening()
    {
        UiThread.Run(() =>
        {
            var text = new TextBlock { Text = "x" };
            Assert.False(LiveRegion.RaiseThroughPeer(text));
        });
    }

    // ------------------------------------------------------------------ the real views

    [Fact]
    public void A_result_banner_on_a_real_panel_announces_with_no_edit_to_the_panel()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var log = new List<string>();
        PanelShell? shell = null;
        LiveRegion.Install();
        using var scope = LiveRegion.UseAnnouncer(element => log.Add(element is InfoBar bar ? $"{bar.Title}|{bar.Message}" : element.GetType().Name));
        try
        {
            ToolsPanelViewModel? vm = null;
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, 1400, 900);
                _ = shell.Show<ToolsPanel>();
                vm = (ToolsPanelViewModel)shell.ViewModel;
            });

            // Let the panel's own first load finish, whatever it says, before counting what a result says.
            UiThread.WaitFor(() => !vm!.IsBusy && !vm.ShowLoading, "the panel's first load");
            UiThread.Run(UiThread.Settle);
            UiThread.Run(() => log.Clear());

            UiThread.Run(() =>
            {
                vm!.ResultTitle = "Rule added";
                vm.ResultMessage = "Blocked shell in prod.";
                vm.IsResultOpen = true;
                shell!.Host.Relayout();
                UiThread.Settle();
            });

            Assert.Equal(new[] { "Rule added|Blocked shell in prod." }, log);
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    // ------------------------------------------------------------------ every declaration in the shipped XAML

    [Fact]
    public void Every_live_region_declared_in_the_shipped_XAML_is_one_the_behaviour_can_watch()
    {
        // The rules the behaviour follows: a text block, an info bar, a control holding a string, or a container with text blocks inside
        // (or an element that raises its own event and says so). A declaration on anything else would still be silent.
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var watchable = new HashSet<string>(StringComparer.Ordinal) { "TextBlock", "InfoBar", "DcCardHeader", "Border", "StackPanel", "Grid" };
        var containers = new HashSet<string>(StringComparer.Ordinal) { "Border", "StackPanel", "Grid" };
        var declarations = 0;
        var problems = new List<string>();

        foreach (var file in Directory.EnumerateFiles(AppDirectory(), "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (var element in XDocument.Load(file).Descendants().Where(e => e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.LiveSetting")))
            {
                declarations++;
                var type = element.Name.LocalName;
                var raisesItself = element.Attributes().Any(a => a.Name.LocalName == "LiveRegion.RaisedByCode");
                if (raisesItself)
                {
                    continue;
                }

                if (!watchable.Contains(type))
                {
                    problems.Add($"{Path.GetFileName(file)}: {type} declares a live region the behaviour cannot watch");
                }
                else if (containers.Contains(type) && !element.Descendants().Any(d => d.Name.LocalName == "TextBlock"))
                {
                    problems.Add($"{Path.GetFileName(file)}: a {type} live region with no text block inside it");
                }
            }
        }

        Assert.Empty(problems);
        Assert.True(declarations >= 38, $"expected the 38 declarations the review counted, found {declarations}");
    }

    private static string AppDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "DefenseClaw.App", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
        }

        throw new FileNotFoundException("DefenseClaw.App\\MainWindow.xaml was not found above the test output directory.");
    }
}
