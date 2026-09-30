using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One of the flyout's three metric rows (Hook Calls, Blocks, Findings): a title and what it counts, the number, and a thin bar whose
/// length is the Mac's own easing of that number (<see cref="TrayFlyoutText"/>). A button: <see cref="OpenCommand"/> opens the dashboard on
/// the panel that explains the number.
/// </summary>
public sealed partial class FlyoutMetricRow : ObservableObject
{
    private readonly string _opens;

    /// <param name="title">"Hook Calls", "Blocks" or "Findings".</param>
    /// <param name="barTone">The bar's tone key.</param>
    /// <param name="opens">The panel the row's button opens, by name ("Logs"), for the screen reader.</param>
    /// <param name="openCommand">What the button does.</param>
    public FlyoutMetricRow(string title, string barTone, string opens, ICommand openCommand)
    {
        Title = title;
        _opens = opens;
        OpenCommand = openCommand;
        _barTone = barTone;
        _value = "—";
        _valueTone = "Neutral";
        _fill = new GridLength(0, GridUnitType.Star);
        _rest = new GridLength(1, GridUnitType.Star);
        _automationName = title;
    }

    /// <summary>"Hook Calls", "Blocks" or "Findings".</summary>
    public string Title { get; }

    public ICommand OpenCommand { get; }

    /// <summary>What the number counts: "latest 500 audit events", "unacknowledged".</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    /// <summary>The number as text; "—" when it could not be read.</summary>
    [ObservableProperty]
    private string _value;

    /// <summary>Tone key of the number's text: <c>Neutral</c> for zero or unknown, else the bar's tone (<c>Primary</c> where the bar is the accent).</summary>
    [ObservableProperty]
    private string _valueTone;

    /// <summary>Tone key of the bar (<c>Accent</c>, <c>Bad</c>, <c>Warn</c>, <c>Ok</c>): the meter's fill brush.</summary>
    [ObservableProperty]
    private string _barTone;

    /// <summary>The filled part of the bar, as a star width, against <see cref="Rest"/>.</summary>
    [ObservableProperty]
    private GridLength _fill;

    [ObservableProperty]
    private GridLength _rest;

    /// <summary>"Hook Calls 46, latest 500 audit events. Opens Logs." - what a screen reader says for the row's button.</summary>
    [ObservableProperty]
    private string _automationName;

    /// <summary>Rewrites <see cref="AutomationName"/> from the row's current number and detail.</summary>
    internal void Describe() => AutomationName = $"{Title} {Value}, {Detail}. Opens {_opens}.";

    /// <summary>Sets the bar to <paramref name="progress"/> (0 to 1); a non-zero <paramref name="count"/> always shows a sliver.</summary>
    internal void SetProgress(double progress, int count)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        if (count > 0)
        {
            clamped = Math.Max(clamped, 0.02);
        }
        else
        {
            clamped = 0;
        }

        Fill = new GridLength(clamped, GridUnitType.Star);
        Rest = new GridLength(1 - clamped, GridUnitType.Star);
    }
}

/// <summary>One connector line: a state dot, its name and mode, and on the right what the gateway says it has done.</summary>
public sealed partial class FlyoutConnectorRow : ObservableObject
{
    public FlyoutConnectorRow(string name)
    {
        Name = name;
        _tone = "Neutral";
        _mode = string.Empty;
        _counts = string.Empty;
        _automationName = name;
    }

    public string Name { get; }

    /// <summary><c>observe</c> / <c>enforce</c> as config.yaml states it; empty when it does not say.</summary>
    [ObservableProperty]
    private string _mode;

    /// <summary>Tone key of the state dot.</summary>
    [ObservableProperty]
    private string _tone;

    /// <summary>"1,934 calls · 0 blocks", or why there are no numbers ("not running", "no live counters").</summary>
    [ObservableProperty]
    private string _counts;

    [ObservableProperty]
    private string _automationName;

    public bool HasMode => Mode.Length > 0;

    partial void OnModeChanged(string value) => OnPropertyChanged(nameof(HasMode));
}

/// <summary>One of the newest unacknowledged findings: a severity mark, its kind and when it happened. A button that opens Alerts.</summary>
public sealed class FlyoutFindingRow
{
    public FlyoutFindingRow(string id, string kind, string severityTone, string severity, string when, string target, ICommand openCommand)
    {
        Id = id;
        Kind = kind;
        Tone = severityTone;
        Severity = severity;
        When = when;
        OpenCommand = openCommand;
        ToolTipText = string.IsNullOrEmpty(target) ? kind : $"{kind}: {target}";
        AutomationName = $"{severity} {kind}, {when}. Opens Alerts.";
    }

    public string Id { get; }

    /// <summary>The event's action ("scan-finding"): what kind of finding it is.</summary>
    public string Kind { get; }

    /// <summary>The design system's tone key for the severity: Critical, High, Medium or Low.</summary>
    public string Tone { get; }

    /// <summary>"Critical", "High", "Medium" or "Low": the mark's colour in words.</summary>
    public string Severity { get; }

    /// <summary>"3w ago".</summary>
    public string When { get; }

    public ICommand OpenCommand { get; }

    /// <summary>The kind and what it is about (a path, a URL), for the hover.</summary>
    public string ToolTipText { get; }

    public string AutomationName { get; }
}

/// <summary>
/// The wording and the bar lengths of the flyout. The strings are the Mac's (<c>MenuBarPopover.swift</c>): "4d up", "0% block rate", "recent
/// block decisions"; the easings are its own, so the same counts draw the same bars.
/// </summary>
internal static class TrayFlyoutText
{
    /// <summary>"4d up", "5h up", "43m up": the gateway's uptime in its largest whole unit (the Mac's <c>uptimeText</c>).</summary>
    public static string Uptime(long uptimeMs)
    {
        var seconds = uptimeMs / 1000;
        if (seconds > 86_400)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / 86_400}d up");
        }

        if (seconds > 3_600)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3_600}h up");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(seconds, 0) / 60}m up");
    }

    /// <summary>"3w ago": how long before <paramref name="now"/> something happened, in its largest whole unit; "just now" under ten seconds.</summary>
    public static string Relative(DateTimeOffset when, DateTimeOffset now)
    {
        if (when == DateTimeOffset.MinValue)
        {
            return "unknown time";
        }

        var elapsed = now - when;
        if (elapsed < TimeSpan.FromSeconds(10))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalSeconds}s ago");
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes}m ago");
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}h ago");
        }

        if (elapsed < TimeSpan.FromDays(7))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalDays}d ago");
        }

        if (elapsed < TimeSpan.FromDays(365))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)(elapsed.TotalDays / 7)}w ago");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)(elapsed.TotalDays / 365)}y ago");
    }

    /// <summary>"0% block rate", "0.4% block rate", "12% block rate", or "recent block decisions" when there were no hook calls to divide by.</summary>
    public static string BlockRate(int blocks, int hookCalls)
    {
        if (hookCalls <= 0)
        {
            return "recent block decisions";
        }

        if (blocks <= 0)
        {
            return "0% block rate";
        }

        var rate = blocks * 100.0 / hookCalls;
        return rate < 1
            ? string.Create(CultureInfo.InvariantCulture, $"{rate:0.0}% block rate")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(rate, MidpointRounding.AwayFromZero)}% block rate");
    }

    /// <summary>Hook Calls bar: <c>n / (n + 250)</c>, so a busy window nearly fills it and a quiet one shows a stub.</summary>
    public static double ActivityProgress(int hookCalls) => hookCalls <= 0 ? 0 : hookCalls / (double)(hookCalls + 250);

    /// <summary>Blocks bar: eight times the block rate, at least 6 % once there is one, or <c>n / 10</c> when there were no hook calls.</summary>
    public static double BlockProgress(int blocks, int hookCalls)
    {
        if (blocks <= 0)
        {
            return 0;
        }

        return hookCalls <= 0
            ? Math.Min(blocks / 10.0, 1)
            : Math.Min(Math.Max(blocks / (double)hookCalls * 8, 0.06), 1);
    }

    /// <summary>Findings bar: <c>n / (n + 10)</c>.</summary>
    public static double FindingsProgress(int findings) => findings <= 0 ? 0 : findings / (double)(findings + 10);

    /// <summary>The design system's tone key for a finding's severity.</summary>
    public static string SeverityTone(AuditSeverity severity) => severity switch
    {
        AuditSeverity.Critical => "Critical",
        AuditSeverity.High => "High",
        AuditSeverity.Medium => "Medium",
        AuditSeverity.Low => "Low",
        _ => "Neutral",
    };

    public static string SeverityWord(AuditSeverity severity) => severity switch
    {
        AuditSeverity.Critical => "Critical",
        AuditSeverity.High => "High",
        AuditSeverity.Medium => "Medium",
        AuditSeverity.Low => "Low",
        _ => "Info",
    };

    /// <summary>The tone key of a connector's <c>/health</c> state text (the Overview panel's classification).</summary>
    public static string ConnectorTone(string? state) => state?.Trim().ToUpperInvariant() switch
    {
        "RUNNING" or "HEALTHY" or "OK" or "ACTIVE" => "Ok",
        "DEGRADED" or "STARTING" or "PENDING" or "PAUSED" => "Warn",
        "FAILED" or "ERROR" or "STOPPED" or "CRASHED" => "Bad",
        _ => "Neutral",
    };
}
