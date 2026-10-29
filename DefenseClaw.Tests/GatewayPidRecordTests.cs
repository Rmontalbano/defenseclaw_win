using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Tests;

/// <summary>
/// <c>gateway.pid</c> as a 1.x gateway writes it on Windows (upstream <c>internal/daemon</c> <c>pidInfo</c>): a record counts only with a positive
/// pid and the executable it names; the pre-1.0 bare number does not (CUST-362).
/// </summary>
public class GatewayPidRecordTests
{
    /// <summary>The copy's path as JSON spells it (each backslash escaped).</summary>
    private const string Copy = @"C:\\TestPF\\DefenseClaw Companion\\AdminGateway\\S-1-5-21-1-2-3-1001\\defenseclaw-gateway.exe";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void The_whole_record_is_read_with_either_line_ending(string newline)
    {
        var text = "{" + newline +
                   "  \"pid\": 11524," + newline +
                   $"  \"executable\": \"{Copy}\"," + newline +
                   "  \"data_dir\": \"C:\\\\TestUsers\\\\operator\\\\.defenseclaw\"," + newline +
                   "  \"start_time\": 1761040800," + newline +
                   "  \"start_identity\": \"1761040800123456700\"" + newline +
                   "}" + newline;

        var record = GatewayPidRecord.TryParse(text);

        Assert.NotNull(record);
        Assert.Equal(11524, record!.Pid);
        Assert.Equal(@"C:\TestPF\DefenseClaw Companion\AdminGateway\S-1-5-21-1-2-3-1001\defenseclaw-gateway.exe", record.Executable);
        Assert.Equal(@"C:\TestUsers\operator\.defenseclaw", record.DataDirectory);
        Assert.Equal(1761040800, record.StartTime);
        Assert.Equal("1761040800123456700", record.StartIdentity);
    }

    [Fact]
    public void The_optional_fields_may_be_missing()
    {
        var record = GatewayPidRecord.TryParse($"{{\"pid\":7,\"executable\":\"{Copy}\"}}");

        Assert.NotNull(record);
        Assert.Null(record!.DataDirectory);
        Assert.Null(record.StartTime);
        Assert.Null(record.StartIdentity);
    }

    [Theory]
    [InlineData("11524")]
    [InlineData("11524\r\n")]
    [InlineData("{\"pid\":11524}")]
    [InlineData("{\"pid\":11524,\"executable\":\"\"}")]
    [InlineData("{\"pid\":11524,\"executable\":7}")]
    [InlineData("{\"pid\":0,\"executable\":\"C:\\\\x\\\\defenseclaw-gateway.exe\"}")]
    [InlineData("{\"pid\":-1,\"executable\":\"C:\\\\x\\\\defenseclaw-gateway.exe\"}")]
    [InlineData("{\"pid\":\"11524\",\"executable\":\"C:\\\\x\\\\defenseclaw-gateway.exe\"}")]
    [InlineData("{\"pid\":11524.5,\"executable\":\"C:\\\\x\\\\defenseclaw-gateway.exe\"}")]
    [InlineData("[11524]")]
    [InlineData("not-a-pid-or-json")]
    [InlineData("{\"pid\":11524,")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_does_not_name_a_process_and_its_file_is_not_a_record(string? text)
    {
        Assert.Null(GatewayPidRecord.TryParse(text));
    }

    [Fact]
    public void An_oversized_file_is_not_read()
    {
        var padding = new string(' ', GatewayPidRecord.MaxBytes);

        Assert.Null(GatewayPidRecord.TryParse($"{{\"pid\":7,\"executable\":\"{Copy}\"}}" + padding));
    }
}
