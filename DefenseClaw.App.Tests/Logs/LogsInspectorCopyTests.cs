using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// CUST-329 (LG5): the Logs inspector's Copy summary and Copy JSON. Copy summary puts the labelled fields the inspector shows on the clipboard, one
/// "Label: value" line each; Copy JSON puts an event's canonical payload there, already display-redacted. Both go through <see cref="DcClipboard"/>,
/// which is faked here through its writer seam, so no test touches the real clipboard.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LogsInspectorCopyTests : IDisposable
{
    private const string Payload = """{"defenseclaw.guardrail.reason":"prompt injection suspected"}""";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly List<string> _written = [];

    public LogsInspectorCopyTests()
    {
        _services = TestServices.Create(_temp);
        DcClipboard.Writer = _written.Add;
    }

    public void Dispose()
    {
        DcClipboard.Writer = null;
        _services.Dispose();
        _temp.Dispose();
    }

    private static LogEntry FileLine() => new(LogLine.Parse("[api] hello", 7));

    private static LogEntry Event() => new(
        new StreamEvent(
            "id-1",
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            "guardrail",
            "guardrail.evaluated",
            "sidecar",
            AuditSeverity.High,
            "block",
            "verdict",
            "prompt injection suspected",
            "claudecode",
            "gateway",
            Payload,
            PayloadOmitted: false)
        {
            SeverityText = "high",
        },
        0,
        "verdicts");

    [Fact]
    public void Copy_summary_of_a_file_line_is_its_fields_as_label_value_lines()
    {
        var entry = FileLine();

        var text = LogsPanelViewModel.CopySummaryText(entry);

        Assert.Equal(string.Join(Environment.NewLine, entry.Fields.Select(f => $"{f.Name}: {f.Value}")), text);
        Assert.Contains("component: api" + Environment.NewLine + "line: 7", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_summary_of_an_event_carries_its_labelled_rows_in_the_inspector_order()
    {
        var entry = Event();

        var lines = LogsPanelViewModel.CopySummaryText(entry).Split(Environment.NewLine);

        Assert.Equal(entry.Fields.Count, lines.Length);
        Assert.StartsWith("Timestamp: 2026-10-0", lines[0], StringComparison.Ordinal);
        Assert.Equal("Event type: verdict", lines[1]);
        Assert.Contains("Event name: guardrail.evaluated", lines);
        Assert.Contains("Severity: HIGH", lines);
    }

    [Fact]
    public void Copy_summary_command_puts_the_summary_on_the_clipboard()
    {
        var panel = new LogsPanelViewModel(_services);
        var entry = Event();

        panel.CopySummaryCommand.Execute(entry);

        Assert.Equal([LogsPanelViewModel.CopySummaryText(entry)], _written);
    }

    [Fact]
    public void Copy_json_command_puts_the_events_redacted_payload_on_the_clipboard()
    {
        var panel = new LogsPanelViewModel(_services);
        var entry = Event();

        panel.CopyJsonCommand.Execute(entry);

        Assert.Equal([entry.Raw], _written);
        Assert.Contains("prompt injection suspected", _written[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_json_does_nothing_for_a_file_line_which_has_no_payload()
    {
        var panel = new LogsPanelViewModel(_services);

        panel.CopyJsonCommand.Execute(FileLine());

        Assert.Empty(_written);
    }

    [Fact]
    public void Copy_commands_do_nothing_without_a_row()
    {
        var panel = new LogsPanelViewModel(_services);

        panel.CopySummaryCommand.Execute(null);
        panel.CopyJsonCommand.Execute(null);

        Assert.Empty(_written);
    }
}
