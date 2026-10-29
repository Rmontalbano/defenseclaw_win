using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Xml;
using System.Xml.Linq;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using UiButton = Wpf.Ui.Controls.Button;

namespace DefenseClaw.App.Tests.Controls;

/// <summary>
/// A WPF control that recognises access keys shows a single "_" in its string content as the mark of the Alt+key shortcut and drops it:
/// "plugin_actions" read "pluginactions" on the Plugins install form (CUST-389), and a doubled "__" is the way to show one. WPF-UI's templates
/// for Button, CheckBox, RadioButton, ToggleButton, ToggleSwitch, MenuItem and the rest set <c>RecognizesAccessKey="True"</c>. These tests hold the
/// line in two places: literals in the XAML (this file reads every <c>*.xaml</c> of the app), and text that comes from the runtime, which is shown
/// through a <see cref="TextBlock"/> so nothing in it is ever read as a key.
/// </summary>
public sealed class AccessKeyXamlTests
{
    /// <summary>
    /// The element names whose templates recognise access keys (WPF-UI 4.3 and WPF's own). Expander and the Dc* controls are left out on purpose:
    /// their headers are plain presenters, and an underscore in one (the "agent_discovery.json" Expander headers) is shown as it is written.
    /// </summary>
    private static readonly HashSet<string> RecognisingControls = new(StringComparer.Ordinal)
    {
        "Button", "CheckBox", "RadioButton", "ToggleButton", "ToggleSwitch", "MenuItem", "TabItem", "HyperlinkButton",
        "DropDownButton", "SplitButton", "Label", "GroupBox",
    };

    /// <summary>
    /// Literals that really are access keys, as "file|text". A menu header such as "E_xit" belongs here; "_File" needs no entry, because a
    /// leading underscore is never reported. The app builds its menus in code (<c>ShellMenuModel</c>, which doubles every underscore it shows), so
    /// the list is empty.
    /// </summary>
    private static readonly HashSet<string> IntentionalAccessKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Where the runtime's own text reaches an access-key control: file, element, bound path. Each must show it in a <see cref="TextBlock"/> child
    /// (the control's Content is then not a string), never as <c>Content="{Binding path}"</c>.
    /// </summary>
    public static TheoryData<string, string, string> RuntimeTextSpots => new()
    {
        { @"Views\Panels\LogsPanel.xaml", "CheckBox", "Name" },
        { @"Views\Panels\AuditPanel.xaml", "Button", "RunFilterText" },
        { @"Views\FirstRun\FirstRunWindow.xaml", "CheckBox", "Label" },
        { @"Views\Panels\OverviewPanel.xaml", "Button", "ScopedAlertsText" },
    };

    [Fact]
    public void No_literal_on_an_access_key_control_has_a_single_underscore_in_a_word()
    {
        var found = new List<string>();
        foreach (var file in XamlFiles())
        {
            var relative = Path.GetRelativePath(AppDirectory(), file);
            foreach (var literal in Literals(file))
            {
                foreach (var run in SingleUnderscores(literal.Text))
                {
                    if (!IntentionalAccessKeys.Contains(relative + "|" + literal.Text))
                    {
                        found.Add($"{relative}({literal.Line}): <{literal.Element} {literal.Property}=\"{literal.Text}\"> reads \"{run}\" with its underscore taken as an access key; write \"__\" to show one");
                    }
                }
            }
        }

        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }

    [Theory]
    [InlineData("Apply the configured plugin_actions policy", new[] { "plugin_actions" })]
    [InlineData("Apply the configured plugin__actions policy", new string[0])]
    [InlineData("_File", new string[0])]
    [InlineData("E_xit", new[] { "E_xit" })]
    [InlineData("a_b__c_d", new[] { "a_b", "c_d" })]
    [InlineData("trailing_", new string[0])]
    [InlineData("Show all connectors", new string[0])]
    public void The_scan_reports_a_single_underscore_between_word_characters_and_nothing_else(string text, string[] expected) =>
        Assert.Equal(expected, SingleUnderscores(text).ToArray());

    [Fact]
    public void The_scan_sees_attributes_and_element_text_of_the_recognising_controls_only()
    {
        const string Xaml = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
              <CheckBox Content="a_b" />
              <ui:Button>c_d</ui:Button>
              <MenuItem Header="e_f" />
              <Expander Header="agent_discovery.json" />
              <TextBlock Text="g_h" />
              <CheckBox Content="{Binding Name}" />
            </Grid>
            """;

        var literals = Literals(XDocument.Parse(Xaml, LoadOptions.SetLineInfo)).Select(l => l.Text).ToArray();

        Assert.Equal(new[] { "a_b", "c_d", "e_f" }, literals);
    }

    [Theory]
    [MemberData(nameof(RuntimeTextSpots))]
    public void Text_from_the_runtime_is_shown_by_a_TextBlock_where_the_control_recognises_access_keys(string file, string element, string path)
    {
        var document = XDocument.Load(Path.Combine(AppDirectory(), file), LoadOptions.SetLineInfo);
        var binding = "{Binding " + path + "}";

        var asString = document.Descendants()
            .Where(e => e.Name.LocalName == element && (string?)e.Attribute("Content") == binding)
            .ToList();
        var asTextBlock = document.Descendants()
            .Where(e => e.Name.LocalName == element
                && e.Elements().Any(c => c.Name.LocalName == "TextBlock" && (string?)c.Attribute("Text") == binding))
            .ToList();

        Assert.Empty(asString);
        Assert.NotEmpty(asTextBlock);
    }

    // ------------------------------------------------------------------------------------------------ the scan

    /// <summary>The runs in <paramref name="text"/> where WPF would read an underscore as an access key: one that is not doubled, with a letter or digit either side.</summary>
    internal static IEnumerable<string> SingleUnderscores(string text) =>
        Regex.Matches(text, @"[A-Za-z0-9]+(?<![_])_(?![_])[A-Za-z0-9]+").Select(m => m.Value);

    private sealed record Literal(string Element, string Property, string Text, int Line);

    private static IEnumerable<Literal> Literals(string file) => Literals(XDocument.Load(file, LoadOptions.SetLineInfo));

    private static IEnumerable<Literal> Literals(XDocument document)
    {
        foreach (var element in document.Descendants().Where(e => RecognisingControls.Contains(e.Name.LocalName)))
        {
            foreach (var property in new[] { "Content", "Header" })
            {
                if (element.Attribute(property) is { } attribute && !attribute.Value.StartsWith('{'))
                {
                    yield return new Literal(element.Name.LocalName, property, attribute.Value, Line(attribute));
                }
            }

            foreach (var text in element.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)))
            {
                yield return new Literal(element.Name.LocalName, "text", text.Value.Trim(), Line(text));
            }
        }
    }

    private static int Line(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppDirectory(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

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


/// <summary>
/// The same rule in rendered views: what a recognising control draws for a "_" in its content, and the real Plugins and Skills install forms
/// (the literal that lost its underscore) and the Logs component list (runtime text) drawing theirs whole.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AccessKeyRenderTests
{
    [Fact]
    public void A_check_box_and_a_button_drop_a_single_underscore_from_string_content_and_keep_a_doubled_one_or_a_TextBlock()
    {
        UiThread.Run(() =>
        {
            var checkBoxes = new[]
            {
                new CheckBox { Content = "plugin_actions" },
                new CheckBox { Content = "plugin__actions" },
                new CheckBox { Content = new TextBlock { Text = "plugin_actions" } },
            };
            var buttons = new[]
            {
                new UiButton { Content = "plugin_actions" },
                new UiButton { Content = "plugin__actions" },
                new UiButton { Content = new TextBlock { Text = "plugin_actions" } },
            };
            var all = new StackPanel();
            foreach (var control in checkBoxes.Cast<Control>().Concat(buttons))
            {
                all.Children.Add(control);
            }

            using var host = new OffscreenHost(all, 400, 300);

            // The premise: WPF-UI's templates read the "_" as the mark of an access key and draw the text without it. If this ever stops
            // being true the guard is obsolete.
            Assert.True(Draws(checkBoxes[0], "pluginactions"), "a check box draws a single underscore");
            Assert.True(Draws(buttons[0], "pluginactions"), "a button draws a single underscore");

            foreach (var control in new ContentControl[] { checkBoxes[1], checkBoxes[2], buttons[1], buttons[2] })
            {
                Assert.True(Draws(control, "plugin_actions"), $"{control.GetType().Name} {control.Content} should draw its underscore");
            }
        });
    }

    [Fact]
    public void The_install_forms_of_Plugins_and_Skills_show_their_policy_name_with_the_underscore()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        foreach (var (plugins, expected) in new[] { (true, "plugin_actions"), (false, "skill_actions") })
        {
            var shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(services, 940, 620);
                if (plugins)
                {
                    _ = shell.Show<PluginsPanel>();
                }
                else
                {
                    _ = shell.Show<SkillsPanel>();
                }

                return shell;
            });
            try
            {
                var vm = (GovernPanelViewModelBase)UiThread.Run(() => shell.ViewModel);
                UiThread.WaitFor(() => vm.State != GovernState.Loading && !vm.IsBusy, "first read finished");
                UiThread.Run(() =>
                {
                    (vm is PluginsPanelViewModel p ? p.ToggleInstallFormCommand : ((SkillsPanelViewModel)vm).ToggleInstallFormCommand).Execute(null);
                    shell.Host.Relayout();

                    var box = VisualTree.Descendants<CheckBox>(shell.Page!)
                        .Single(c => c.Content is string text && text.StartsWith("Apply the configured", StringComparison.Ordinal));
                    Assert.True(Draws(box, $"Apply the configured {expected} policy based on scan severity"), $"the {(plugins ? "Plugins" : "Skills")} form lost the underscore in {expected}");
                });
            }
            finally
            {
                UiThread.Run(shell.Dispose);
            }
        }
    }

    [Fact]
    public void A_log_component_with_an_underscore_is_listed_whole()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var shell = new PanelShell(services, 1200, 800);
            _ = shell.Show<LogsPanel>();
            return shell;
        });
        try
        {
            UiThread.Run(() =>
            {
                var vm = (LogsPanelViewModel)shell.ViewModel;
                vm.ComponentFilters.Add(new ComponentFilterOption("guardrail_proxy"));
                shell.Host.Relayout();

                var box = VisualTree.Descendants<CheckBox>(shell.Page!).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "guardrail_proxy");
                Assert.True(Draws(box, "guardrail_proxy"), "the component name lost its underscore");
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    /// <summary>
    /// True when the text the control's content presenter draws is as wide as <paramref name="text"/> set in the same font. The width is the test
    /// because the text of an access-key presenter cannot be read back: its run keeps the underscore in the string and draws it with no width.
    /// </summary>
    private static bool Draws(ContentControl control, string text)
    {
        var presenter = VisualTree.Descendants<ContentPresenter>(control).First(p => p.Content is string or TextBlock);
        var drawn = VisualTree.Descendants<TextBlock>(presenter).Single();
        var reference = new TextBlock
        {
            Text = text,
            FontFamily = drawn.FontFamily,
            FontSize = drawn.FontSize,
            FontStyle = drawn.FontStyle,
            FontWeight = drawn.FontWeight,
            FontStretch = drawn.FontStretch,
        };
        reference.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Abs(drawn.ActualWidth - reference.DesiredSize.Width) < 0.01;
    }
}
