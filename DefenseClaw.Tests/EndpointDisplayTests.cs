using System.Text.RegularExpressions;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="EndpointDisplay"/> (CUST-272): the one place a destination's address is reduced to what may be shown. Every credential here is a made-up
/// value that starts with <c>synth</c> (the convention <c>.gitleaks.toml</c> allows), and every host is under <c>example.test</c>.
/// </summary>
public class EndpointDisplayTests
{
    /// <summary>Everything a credential-carrying address could leak: no test output may contain any of these.</summary>
    private static readonly string[] Secrets =
    {
        "synthuser", "synthpass", "synthkey", "synthtoken", "synthfrag", "synthpath-secret", "api_key", "token=", "secret",
    };

    [Theory]
    [InlineData("https://collector.example.test:4318/v1/logs", "collector.example.test:4318")]
    [InlineData("https://collector.example.test/v1/logs", "collector.example.test")]
    [InlineData("https://synthuser:synthpass@collector.example.test/v1/logs?api_key=synthkey#synthfrag", "collector.example.test")]
    [InlineData("https://synthuser:synthpass@collector.example.test:8088", "collector.example.test:8088")]
    [InlineData("https://synthuser@collector.example.test", "collector.example.test")]
    [InlineData("https://synthuser:p@ss@collector.example.test/", "collector.example.test")]
    [InlineData("https://collector.example.test?api_key=synthkey", "collector.example.test")]
    [InlineData("https://collector.example.test#synthfrag", "collector.example.test")]
    [InlineData("https://collector.example.test/services/synthpath-secret/hook", "collector.example.test")]
    [InlineData("https://collector.example.test\\services\\synthpath-secret", "collector.example.test")]
    [InlineData("HTTP://Collector.Example.Test:80/x", "Collector.Example.Test:80")]
    [InlineData("collector.example.test:4317", "collector.example.test:4317")]
    [InlineData("collector.example.test", "collector.example.test")]
    [InlineData("collector.example.test/v1?api_key=synthkey", "collector.example.test")]
    [InlineData("//collector.example.test/x", "collector.example.test")]
    [InlineData("  https://collector.example.test:4318/  ", "collector.example.test:4318")]
    [InlineData("https://[2001:db8::1]:4318/v1", "[2001:db8::1]:4318")]
    [InlineData("https://[2001:db8::1]/v1?api_key=synthkey", "[2001:db8::1]")]
    [InlineData("https://[fe80::1%eth0]:4318", "[fe80::1%eth0]:4318")]
    [InlineData("127.0.0.1:9464", "127.0.0.1:9464")]
    [InlineData("grpc://collector.example.test:4317", "collector.example.test:4317")]
    [InlineData("https://bücher.example.test/x", "bücher.example.test")]
    [InlineData("https://host_name.example.test:1", "host_name.example.test:1")]
    public void An_address_is_reduced_to_its_host_and_port(string address, string expected)
    {
        var shown = EndpointDisplay.Host(address);

        Assert.Equal(expected, shown);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_nothing(string? address) => Assert.Equal(string.Empty, EndpointDisplay.Host(address));

    [Theory]
    [InlineData("https://")]
    [InlineData("https://synthuser:synthpass@")]
    [InlineData("https://synthuser:synthpass@/v1")]
    [InlineData("https://collector.example.test:notaport/x")]
    [InlineData("https://collector.example.test:99999")]
    [InlineData("https://collector.example.test:123456")]
    [InlineData("ht!tp://collector.example.test")]
    [InlineData("://collector.example.test")]
    [InlineData("https://collector example.test/")]
    [InlineData("https://collector;key=synthkey.example.test/")]
    [InlineData("https://collector.example.test=synthtoken/")]
    [InlineData("https://synthuser:123/synthpass@collector.example.test/")]
    [InlineData("https://synthuser:synthpass\\x@collector.example.test/")]
    [InlineData("https://collector.example.test/synthpath-secret@elsewhere.example.test/")]
    [InlineData("https://[not-an-ipv6]/")]
    [InlineData("https://[]/")]
    [InlineData("https://[2001:db8::1]junk")]
    [InlineData("https://a:b:c/")]
    [InlineData("unix:///var/run/example.sock")]
    [InlineData("file:///C:/logs/example.log")]
    [InlineData("https://\u202Ecollector.example.test/")]
    public void An_address_with_no_host_that_can_be_vouched_for_is_unreadable_and_says_nothing_of_the_original(string address)
    {
        var shown = EndpointDisplay.Host(address);

        Assert.Equal(EndpointDisplay.Unreadable, shown);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_address_longer_than_any_endpoint_is_unreadable()
    {
        var longest = "https://collector.example.test/" + new string('x', 3000);

        Assert.Equal(EndpointDisplay.Unreadable, EndpointDisplay.Host(longest));
    }

    [Fact]
    public void Whatever_it_is_given_the_answer_is_a_host_or_the_placeholder_and_never_a_piece_of_the_input()
    {
        var inputs = new[]
        {
            "https://synthuser:synthpass@collector.example.test:4318/v1/logs?api_key=synthkey#synthfrag",
            "synthuser:synthpass@collector.example.test",
            "https://collector.example.test/synthpath-secret?token=synthtoken",
            "https://synthuser:synthpass@[2001:db8::1]:1/?x=synthkey",
            "key=synthkey;host=collector.example.test",
            "https://synthuser:synthpass@collector.example.test:99999/?x=synthkey",
            "Bearer synthtoken@collector.example.test",
            "https://synthuser:synthpass@",
            "?api_key=synthkey",
            "#synthfrag",
            "@",
            "https://@/",
        };

        var shape = new Regex(@"^[\p{L}\p{N}._\-]+(:\d{1,5})?$|^\[[0-9A-Fa-f:.%A-Za-z0-9_\-]+\](:\d{1,5})?$", RegexOptions.CultureInvariant);
        foreach (var input in inputs)
        {
            var shown = EndpointDisplay.Host(input);

            Assert.True(shown == EndpointDisplay.Unreadable || shape.IsMatch(shown), $"'{shown}' is neither a host nor the placeholder");
            Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- free text ----

    [Fact]
    public void A_delivery_error_that_quotes_the_address_is_cut_to_the_host()
    {
        var error = "Post \"https://synthuser:synthpass@collector.example.test:4318/v1/logs?api_key=synthkey\": context deadline exceeded";

        var shown = EndpointDisplay.ScrubText(error);

        Assert.Equal("Post \"https://collector.example.test:4318\": context deadline exceeded", shown);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_address_in_a_sentence_is_cut_and_a_credential_that_is_not_in_one_is_masked()
    {
        var text = "primary https://synthuser:synthpass@one.example.test/a?x=synthkey failed, then http://two.example.test:8080/b?token=synthtoken; retry with token=synthtoken-synthtoken";

        var shown = EndpointDisplay.ScrubText(text);

        Assert.Contains("https://one.example.test", shown, StringComparison.Ordinal);
        Assert.Contains("http://two.example.test:8080", shown, StringComparison.Ordinal);
        Assert.All(Secrets.Where(s => s != "token="), secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("synthtoken-synthtoken", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_in_a_sentence_that_cannot_be_read_is_the_placeholder()
    {
        var shown = EndpointDisplay.ScrubText("dial https://synthuser:synthpass@:99999/x?synthkey refused");

        Assert.Contains(EndpointDisplay.Unreadable, shown, StringComparison.Ordinal);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("connection refused", "connection refused")]
    [InlineData("  tls: handshake failure  ", "tls: handshake failure")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void An_ordinary_sentence_is_left_alone(string? text, string expected) => Assert.Equal(expected, EndpointDisplay.ScrubText(text));

    [Fact]
    public void Control_and_bidirectional_characters_are_written_out_and_the_text_is_cut_to_the_limit()
    {
        Assert.Equal("line one\\nline two", EndpointDisplay.ScrubText("line one\nline two"));
        Assert.Contains("\\u202E", EndpointDisplay.ScrubText("a\u202Eb"), StringComparison.Ordinal);

        var cut = EndpointDisplay.ScrubText(new string('a', 500), 40);
        Assert.Equal(40, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void Hostile_text_is_masked_whole_rather_than_hanging()
    {
        var hostile = "https://" + new string('a', 20_000) + "@" + new string('b', 20_000) + "/" + string.Concat(Enumerable.Repeat("https://x@", 2_000));

        var shown = EndpointDisplay.ScrubText(hostile);

        Assert.True(shown.Length <= 240, $"{shown.Length} characters");
        Assert.DoesNotContain(new string('a', 100), shown, StringComparison.Ordinal);
    }
}
