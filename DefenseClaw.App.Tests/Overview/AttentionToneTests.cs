using System.Text.Json;
using System.Xml.Linq;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// Severity to tone on the Overview panel (CUST-180). The panel never picks a colour itself: it hands the shared
/// <c>Dc*</c> tone styles a <c>Tag</c> key, and the theme turns the key into red / amber / blue / green / grey. So the
/// two halves are tested separately: what the view-model asks for, and what the theme gives for it.
/// </summary>
public sealed class AttentionToneTests : IDisposable
{
    /// <summary>The five looks a tone key can produce. Mirrors the TONES table at the top of Themes\DefenseClaw.xaml.</summary>
    public enum Tone
    {
        Red,
        Amber,
        Blue,
        Green,
        Neutral,
    }

    /// <summary>Every key the theme knows a tone for; anything else (Low, Info, Neutral, unset) is grey.</summary>
    private static Tone ToneOf(string key) => key switch
    {
        "Critical" or "Bad" => Tone.Red,
        "High" or "Warn" => Tone.Amber,
        "Medium" => Tone.Blue,
        "Ok" => Tone.Green,
        _ => Tone.Neutral,
    };

    private static readonly string[] KnownKeys = { "Critical", "Bad", "High", "Warn", "Medium", "Ok", "Low", "Info", "Neutral" };

    private readonly TempDirectory _temp = new();
    private DefenseClaw.App.Services.AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private OverviewPanelViewModel Panel(string? configYaml = null)
    {
        _services = TestServices.Create(_temp, configYaml);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewaySnapshot Snapshot(
        AppGatewayState state,
        int criticalAlerts = 0,
        IReadOnlyList<GatewayAlert>? recent = null,
        FailModeDrift? drift = null,
        string? alertsUnavailable = null) => new()
    {
        State = state,
        Detail = "detail",
        CriticalAlertCount = criticalAlerts,
        RecentAlerts = recent ?? Array.Empty<GatewayAlert>(),
        FailModeDrift = drift,
        AlertsUnavailable = alertsUnavailable,
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static GatewayAlert Alert(string id, string severity) => new()
    {
        Id = id,
        Timestamp = DateTimeOffset.UtcNow,
        Severity = severity,
        Structured = new Dictionary<string, JsonElement>
        {
            [GatewayAlert.Keys.RuleId] = JsonSerializer.SerializeToElement("CMD-ENV-DUMP"),
        },
    };

    private static FailModeDrift Drift(string env, string gateway, string? guardrailMode) => new()
    {
        EnvFailMode = env,
        GatewayFailMode = gateway,
        GatewaySource = FailModeDrift.StatusSource,
        GuardrailMode = guardrailMode,
        SettingsPath = "C:\\Users\\someone\\.claude\\settings.json",
    };

    private const string ObserveFailClosedConfig =
        "guardrail:\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: closed\n";

    private static AttentionRow RowTitled(OverviewPanelViewModel vm, string titleStart) =>
        Assert.Single(vm.Attention, r => r.Title.StartsWith(titleStart, StringComparison.Ordinal));

    // ------------------------------------------------------------------ the row CUST-180 was about

    [Fact]
    public void The_HIGH_findings_row_is_amber_not_blue()
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.Running, recent: new[] { Alert("1", "HIGH"), Alert("2", "HIGH") }));

        var row = RowTitled(vm, "2 HIGH findings");
        Assert.Equal(Tone.Amber, ToneOf(row.SeverityKey));
        Assert.NotEqual(Tone.Blue, ToneOf(row.SeverityKey));
    }

    [Fact]
    public void The_HIGH_findings_row_and_the_high_severity_tile_use_the_same_tone_key()
    {
        // The Alerts and Audit panels and the Overview severity tiles all key HIGH as "High"; the attention row must too.
        var vm = Panel();
        vm.Apply(Snapshot(AppGatewayState.Running, recent: new[] { Alert("1", "HIGH") }));

        Assert.Equal("High", RowTitled(vm, "1 HIGH finding").SeverityKey);
    }

    // ------------------------------------------------------------------ every other row that names its own severity

    [Fact]
    public void The_CRITICAL_alerts_row_is_red()
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.Running, criticalAlerts: 2));

        Assert.Equal(Tone.Red, ToneOf(RowTitled(vm, "2 CRITICAL alerts").SeverityKey));
    }

    [Fact]
    public void A_fail_mode_mismatch_in_config_yaml_is_amber()
    {
        var vm = Panel(ObserveFailClosedConfig);

        vm.Apply(Snapshot(AppGatewayState.Running));

        Assert.Equal(Tone.Amber, ToneOf(RowTitled(vm, "claudecode: observe mode").SeverityKey));
    }

    [Theory]
    [InlineData("closed", "open", "observe", Tone.Red)]
    [InlineData("open", "closed", "observe", Tone.Amber)]
    [InlineData("closed", "closed", "enforce", Tone.Amber)]
    public void A_settings_json_fail_mode_override_is_red_only_in_the_dangerous_pairing(string env, string gateway, string guardrailMode, Tone expected)
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.Running, drift: Drift(env, gateway, guardrailMode)));

        Assert.Equal(expected, ToneOf(RowTitled(vm, "claudecode: settings.json overrides").SeverityKey));
    }

    // ------------------------------------------------------------------ the gateway-state rows

    [Theory]
    [InlineData(AppGatewayState.NotInstalled, Tone.Red)]
    [InlineData(AppGatewayState.GatewayStopped, Tone.Amber)]
    [InlineData(AppGatewayState.WslGatewayDetected, Tone.Amber)]
    [InlineData(AppGatewayState.Degraded, Tone.Blue)]
    [InlineData(AppGatewayState.NotInitialized, Tone.Blue)]
    public void Each_gateway_problem_state_has_the_tone_of_its_place_on_the_severity_ladder(AppGatewayState state, Tone expected)
    {
        var vm = Panel();

        vm.Apply(Snapshot(state));

        Assert.Equal(expected, ToneOf(Assert.Single(vm.Attention).SeverityKey));
    }

    [Fact]
    public void An_unreadable_config_yaml_is_red()
    {
        var vm = Panel("gateway: [unclosed\n");

        vm.Apply(Snapshot(AppGatewayState.Running));

        Assert.Equal(Tone.Red, ToneOf(RowTitled(vm, "config.yaml could not be read").SeverityKey));
    }

    [Fact]
    public void Nothing_needs_attention_is_green_and_the_informational_rows_are_grey()
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.Running));
        Assert.Equal(Tone.Green, ToneOf(Assert.Single(vm.Attention).SeverityKey));

        vm.Apply(Snapshot(AppGatewayState.Running, alertsUnavailable: "gateway: not connected"));
        Assert.Equal(Tone.Neutral, ToneOf(RowTitled(vm, "Alerts are not being served").SeverityKey));

        vm.Apply(GatewaySnapshot.Initial);
        Assert.Equal(Tone.Neutral, ToneOf(Assert.Single(vm.Attention).SeverityKey));
    }

    [Theory]
    [InlineData(AppGatewayState.Running, "Ok")]
    [InlineData(AppGatewayState.Degraded, "Warn")]
    [InlineData(AppGatewayState.WslGatewayDetected, "Warn")]
    [InlineData(AppGatewayState.Unknown, "Neutral")]
    [InlineData(AppGatewayState.GatewayStopped, "Bad")]
    [InlineData(AppGatewayState.NotInstalled, "Bad")]
    [InlineData(AppGatewayState.NotInitialized, "Bad")]
    public void The_headline_dot_keys_follow_the_same_vocabulary(AppGatewayState state, string expectedKey)
    {
        var vm = Panel();

        vm.Apply(Snapshot(state));

        Assert.Equal(expectedKey, vm.GatewayStateKey);
    }

    // ------------------------------------------------------------------ no key the theme does not know

    [Fact]
    public void Every_key_the_panel_can_emit_is_one_the_theme_has_a_tone_for()
    {
        // A typo ("Hgh") would fall through to neutral grey without a single failing assertion elsewhere.
        var vm = Panel(ObserveFailClosedConfig);
        var seen = new List<string>();

        var snapshots = new[]
        {
            GatewaySnapshot.Initial,
            Snapshot(AppGatewayState.Running),
            Snapshot(AppGatewayState.Degraded),
            Snapshot(AppGatewayState.GatewayStopped),
            Snapshot(AppGatewayState.NotInstalled),
            Snapshot(AppGatewayState.NotInitialized),
            Snapshot(AppGatewayState.WslGatewayDetected),
            Snapshot(
                AppGatewayState.Running,
                criticalAlerts: 1,
                recent: new[] { Alert("1", "HIGH") },
                drift: Drift("closed", "open", "observe"),
                alertsUnavailable: "gateway: not connected"),
        };

        foreach (var snapshot in snapshots)
        {
            vm.Apply(snapshot);
            seen.AddRange(vm.Attention.Select(r => r.SeverityKey));
            seen.Add(vm.GatewayStateKey);
        }

        Assert.All(seen, key => Assert.Contains(key, KnownKeys));
    }

    // ------------------------------------------------------------------ the theme half

    [Theory]
    [InlineData("DcBadge", "Background", "SystemFillColor{0}BackgroundBrush")]
    [InlineData("DcToneText", "Foreground", "SystemFillColor{0}Brush")]
    [InlineData("DcToneDot", "Fill", "SystemFillColor{0}Brush")]
    [InlineData("DcToneBar", "Background", "SystemFillColor{0}Brush")]
    [InlineData("DcToneTile", "Background", "SystemFillColor{0}BackgroundBrush")]
    [InlineData("DcValue", "Foreground", "SystemFillColor{0}Brush")]
    public void The_theme_paints_High_and_Warn_amber_and_Medium_blue(string style, string property, string brushPattern)
    {
        var triggers = ToneTriggers(style, property);

        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, brushPattern, "Caution"), triggers["High"]);
        Assert.Equal(triggers["High"], triggers["Warn"]);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, brushPattern, "Attention"), triggers["Medium"]);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, brushPattern, "Critical"), triggers["Critical"]);
        Assert.Equal(triggers["Critical"], triggers["Bad"]);
    }

    /// <summary>Tag value → the brush key a style's <c>Tag</c> trigger sets <paramref name="property"/> to, read from the theme's source.</summary>
    private static Dictionary<string, string> ToneTriggers(string styleKey, string property)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var document = XDocument.Load(ThemePath());
        var style = document.Descendants(presentation + "Style")
            .Single(s => (string?)s.Attribute(xaml + "Key") == styleKey);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var trigger in style.Descendants(presentation + "Trigger").Where(t => (string?)t.Attribute("Property") == "Tag"))
        {
            var setter = trigger.Elements(presentation + "Setter").Single(s => (string?)s.Attribute("Property") == property);
            var value = (string)setter.Attribute("Value")!;

            // "{DynamicResource SystemFillColorCautionBrush}" → "SystemFillColorCautionBrush"
            result[(string)trigger.Attribute("Value")!] = value.Trim('{', '}').Split(' ', 2)[1];
        }

        return result;
    }

    private static string ThemePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "DefenseClaw.App", "Themes", "DefenseClaw.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Themes\\DefenseClaw.xaml was not found above the test output directory.");
    }
}
