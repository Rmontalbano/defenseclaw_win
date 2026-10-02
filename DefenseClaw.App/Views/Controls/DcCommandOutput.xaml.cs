using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// The one way to show a command's output: selectable, copyable, monospace. <see cref="Text"/> is what the runner already
/// scrubbed; the box shows it and the Copy output button copies what the box shows - never anything else, never unscrubbed text.
/// <para>
/// Output longer than <see cref="MaxDisplayChars"/> is trimmed to its tail, with a first line saying how much is not shown
/// (the Activity panel keeps the invocation); what is copied is what is displayed, note included.
/// </para>
/// <para>Size it with <c>MaxHeight</c> (the box scrolls inside it) or leave it to grow with its text.</para>
/// </summary>
public partial class DcCommandOutput : UserControl
{
    /// <summary>A transcript past this many characters is shown from its tail. Generous: a TextBox lays out all its text, so it cannot be unbounded.</summary>
    public const int DefaultMaxDisplayChars = 200_000;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DcCommandOutput), new PropertyMetadata(string.Empty, OnDisplayInputChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(DcCommandOutput), new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty OutputNameProperty = DependencyProperty.Register(
        nameof(OutputName), typeof(string), typeof(DcCommandOutput), new PropertyMetadata("Command output", OnOutputNameChanged));

    public static readonly DependencyProperty WrapLinesProperty = DependencyProperty.Register(
        nameof(WrapLines), typeof(bool), typeof(DcCommandOutput), new PropertyMetadata(true, OnWrapChanged));

    public static readonly DependencyProperty FollowTailProperty = DependencyProperty.Register(
        nameof(FollowTail), typeof(bool), typeof(DcCommandOutput), new PropertyMetadata(false));

    public static readonly DependencyProperty MaxDisplayCharsProperty = DependencyProperty.Register(
        nameof(MaxDisplayChars), typeof(int), typeof(DcCommandOutput), new PropertyMetadata(DefaultMaxDisplayChars, OnDisplayInputChanged));

    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    public DcCommandOutput()
    {
        InitializeComponent();
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            CopiedText.Visibility = Visibility.Collapsed;
        };
        Box.TextChanged += (_, _) => CopyButton.IsEnabled = Box.Text.Length > 0;
        ApplyOutputName();
        ApplyWrap();
        CopyButton.IsEnabled = false;
    }

    /// <summary>Replaces what writes to the clipboard (tests); the default is <see cref="Clipboard.SetText(string)"/>.</summary>
    public Action<string>? ClipboardWriter { get; set; }

    /// <summary>The output, as it should be displayed (already scrubbed).</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>An optional caption above the box.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The box's UI Automation name.</summary>
    public string OutputName
    {
        get => (string)GetValue(OutputNameProperty);
        set => SetValue(OutputNameProperty, value);
    }

    /// <summary>Wrap long lines (default) or scroll sideways.</summary>
    public bool WrapLines
    {
        get => (bool)GetValue(WrapLinesProperty);
        set => SetValue(WrapLinesProperty, value);
    }

    /// <summary>Keep the newest line in view while output arrives, unless the operator has scrolled up.</summary>
    public bool FollowTail
    {
        get => (bool)GetValue(FollowTailProperty);
        set => SetValue(FollowTailProperty, value);
    }

    public int MaxDisplayChars
    {
        get => (int)GetValue(MaxDisplayCharsProperty);
        set => SetValue(MaxDisplayCharsProperty, value);
    }

    /// <summary>The text the box is showing, which is also exactly what Copy output copies.</summary>
    public string DisplayedText => Box.Text;

    /// <summary>The read-only box, for tests.</summary>
    public TextBox OutputBox => Box;

    public Button CopyOutputButton => CopyButton;

    /// <summary>Copies the displayed text. False when there is nothing displayed or the clipboard refused it.</summary>
    public bool CopyOutput()
    {
        var text = Box.Text;
        if (text.Length == 0)
        {
            return false;
        }

        try
        {
            if (ClipboardWriter is { } write)
            {
                write(text);
            }
            else
            {
                Clipboard.SetText(text);
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // The clipboard is locked by another process; say so rather than claim a copy.
            CopiedText.Text = "Could not use the clipboard";
            ShowCopiedNote();
            return false;
        }

        CopiedText.Text = "Copied";
        ShowCopiedNote();
        return true;
    }

    /// <summary>What the box shows for <paramref name="text"/>: the text itself, or its tail behind a note when it is longer than <paramref name="max"/>.</summary>
    public static string ForDisplay(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (max <= 0 || text.Length <= max)
        {
            return text;
        }

        // Cut at a line start so the first shown line is whole.
        var start = text.Length - max;
        var nl = text.IndexOf('\n', start);
        if (nl >= 0 && nl < text.Length - 1)
        {
            start = nl + 1;
        }

        var hidden = start.ToString("N0", CultureInfo.CurrentCulture);
        return $"… {hidden} earlier characters are not shown (the Activity panel keeps the whole run) …{Environment.NewLine}" + text[start..];
    }

    private void ShowCopiedNote()
    {
        CopiedText.Visibility = Visibility.Visible;
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => _ = CopyOutput();

    private void ApplyDisplay()
    {
        var shown = ForDisplay(Text, MaxDisplayChars);
        if (string.Equals(shown, Box.Text, StringComparison.Ordinal))
        {
            return;
        }

        var follow = FollowTail && Box.VerticalOffset + Box.ViewportHeight >= Box.ExtentHeight - 2;
        Box.Text = shown;
        if (follow)
        {
            Box.ScrollToEnd();
        }
    }

    private void ApplyOutputName() => AutomationProperties.SetName(Box, OutputName);

    private void ApplyWrap() => Box.TextWrapping = WrapLines ? TextWrapping.Wrap : TextWrapping.NoWrap;

    private static void OnDisplayInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DcCommandOutput)d).ApplyDisplay();

    private static void OnOutputNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DcCommandOutput)d).ApplyOutputName();

    private static void OnWrapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DcCommandOutput)d).ApplyWrap();

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (DcCommandOutput)d;
        var title = (string)e.NewValue;
        self.TitleText.Text = title;
        self.TitleText.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
    }
}
