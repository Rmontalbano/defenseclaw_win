using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-268: <see cref="ConfigFieldValidator"/> is the 0.8.10 TUI's <c>validate_config_field</c> (<c>setup_state.py:479-524</c>), so these are a table per rule:
/// the key and kind a field has, the value typed, what the TUI answers. A message is matched by its first words (the TUI's own), never in full.
/// <para>
/// Every credential-shaped input is built from fragments (<see cref="Fake"/>), so no line of this file reads as one to a secret scanner, and
/// none of them is a real credential.
/// </para>
/// </summary>
public class ConfigFieldValidatorTests
{
    private const FormFieldKind Bool = FormFieldKind.Bool;
    private const FormFieldKind Int = FormFieldKind.Int;
    private const FormFieldKind Text = FormFieldKind.String;
    private const FormFieldKind Env = FormFieldKind.EnvName;
    private const FormFieldKind Choice = FormFieldKind.Choice;

    /// <summary>A value that has the shape of a credential, <paramref name="length"/> characters long, built so its parts are never written together.</summary>
    private static string Fake(string prefix, int length) => prefix + new string('q', length - prefix.Length);

    private static string Sk(int length = 40) => Fake("s" + "k-", length);

    private static FieldValidation Check(string key, FormFieldKind kind, string value, string? original = "previous", IReadOnlyList<string>? options = null) =>
        ConfigFieldValidator.Validate(key, kind, value, original, options);

    private static void AssertOk(FieldValidation result)
    {
        Assert.Equal(FieldSeverity.None, result.Severity);
        Assert.True(result.IsOk);
        Assert.Equal(string.Empty, result.Message);
    }

    private static void AssertError(FieldValidation result, string messageStart)
    {
        Assert.Equal(FieldSeverity.Error, result.Severity);
        Assert.True(result.IsError);
        Assert.StartsWith(messageStart, result.Message, StringComparison.Ordinal);
    }

    private static void AssertWarning(FieldValidation result, string messageStart)
    {
        Assert.Equal(FieldSeverity.Warning, result.Severity);
        Assert.True(result.IsWarning);
        Assert.StartsWith(messageStart, result.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ bool

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("  true  ")]
    public void A_bool_is_true_or_false(string value) => AssertOk(Check("gateway.tls", Bool, value));

    [Theory]
    [InlineData("yes")]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("")]
    public void Anything_else_is_not_a_bool(string value) =>
        AssertError(Check("gateway.tls", Bool, value), "expected true or false");

    // ------------------------------------------------------------------ choice

    [Fact]
    public void A_choice_must_be_one_of_its_options_and_the_message_lists_them_in_order()
    {
        var options = new[] { "observe", "action" };

        AssertOk(Check("guardrail.mode", Choice, "action", options: options));
        AssertError(Check("guardrail.mode", Choice, "enforce", options: options), "choose one of: observe, action");
        AssertError(Check("guardrail.mode", Choice, "Action", options: options), "choose one of:");
    }

    [Fact]
    public void A_blank_option_is_named_in_the_message_instead_of_printing_a_bare_comma()
    {
        var result = Check("claude_code.fail_mode", Choice, "sideways", options: new[] { string.Empty, "open", "closed" });

        AssertError(result, "choose one of: (blank), open, closed");
    }

    [Fact]
    public void A_blank_choice_is_fine_when_blank_is_an_option_and_an_error_when_it_is_not()
    {
        AssertOk(Check("claude_code.fail_mode", Choice, string.Empty, options: new[] { string.Empty, "open", "closed" }));
        AssertError(Check("guardrail.mode", Choice, string.Empty, options: new[] { "observe", "action" }), "choose one of:");
    }

    [Fact]
    public void A_choice_without_options_accepts_anything()
    {
        AssertOk(Check("guardrail.mode", Choice, "whatever", options: null));
        AssertOk(Check("guardrail.mode", Choice, "whatever", options: Array.Empty<string>()));
    }

    // ------------------------------------------------------------------ unset and still unset (pin setup_state.py:600-602)

    [Theory]
    [InlineData(Bool)]
    [InlineData(Int)]
    [InlineData(Choice)]
    public void A_value_that_was_unset_and_still_is_never_complains(FormFieldKind kind) =>
        AssertOk(Check("guardrail.mode", kind, string.Empty, original: string.Empty, options: new[] { "observe", "action" }));

    [Theory]
    [InlineData(Bool, "expected true or false")]
    [InlineData(Int, "expected an integer")]
    [InlineData(Choice, "choose one of:")]
    public void A_value_that_was_set_and_is_now_empty_does(FormFieldKind kind, string message) =>
        AssertError(Check("guardrail.mode", kind, string.Empty, original: "x", options: new[] { "observe", "action" }), message);

    // ------------------------------------------------------------------ ports

    [Theory]
    [InlineData("gateway.api_port", "1")]
    [InlineData("gateway.api_port", "18970")]
    [InlineData("gateway.port", "65535")]
    [InlineData("guardrail.port", " 8080 ")]
    [InlineData("gateway.api_port", "+80")]
    [InlineData("gateway.api_port", "1_000")]
    public void A_port_from_1_to_65535_is_fine(string key, string value) => AssertOk(Check(key, Int, value));

    [Theory]
    [InlineData("gateway.api_port", "70000")]
    [InlineData("gateway.api_port", "65536")]
    [InlineData("gateway.api_port", "0")]
    [InlineData("gateway.port", "-1")]
    [InlineData("guardrail.port", "99999999999999999999")]
    [InlineData("proxy_port", "0")]
    public void A_port_outside_1_to_65535_is_an_error(string key, string value) =>
        AssertError(Check(key, Int, value), "port must be between 1 and 65535");

    [Theory]
    [InlineData("openshell.ingress_port")]
    [InlineData("openshell.egress_port")]
    public void The_openshell_ports_may_be_0_which_derives_them_from_the_api_port(string key)
    {
        AssertOk(Check(key, Int, "0"));
        AssertOk(Check(key, Int, "65535"));
        AssertError(Check(key, Int, "65536"), "port must be between 0 and 65535");
        AssertError(Check(key, Int, "-1"), "port must be between 0 and 65535");
    }

    [Theory]
    [InlineData("observability.max_export_batch_size", "100000")]
    [InlineData("observability.transport_retries", "0")]
    [InlineData("ai_discovery.import_depth", "0")]
    [InlineData("support.level", "70000")]
    public void A_key_that_only_contains_the_letters_port_is_not_a_port(string key, string value) =>
        AssertOk(Check(key, Int, value));

    [Theory]
    [InlineData("gateway.api_port", true)]
    [InlineData("gateway.port", true)]
    [InlineData("openshell.egress_port", true)]
    [InlineData("port", true)]
    [InlineData("a.b.proxy-port", true)]
    [InlineData("gateway.PORT", true)]
    [InlineData("observability.max_export_batch_size", false)]
    [InlineData("a.transport", false)]
    [InlineData("a.ports", false)]
    public void Port_is_a_whole_word_of_the_last_segment(string key, bool expected) =>
        Assert.Equal(expected, ConfigFieldValidator.IsPortKey(key));

    // ------------------------------------------------------------------ integers

    [Theory]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("1__0")]
    [InlineData("_1")]
    [InlineData("1_")]
    [InlineData("--1")]
    [InlineData("")]
    public void An_int_must_be_an_integer(string value) =>
        AssertError(Check("watch.debounce_ms", Int, value, original: "5"), "expected an integer");

    // ------------------------------------------------------------------ non-negative timeouts

    [Theory]
    [InlineData("llm.timeout")]
    [InlineData("guardrail.judge.adjudication_timeout")]
    [InlineData("gateway.watchdog.interval")]
    [InlineData("llm.max_retries")]
    [InlineData("gateway.reconnect_retries")]
    [InlineData("gateway.max_reconnect_ms")]
    [InlineData("llm.max_tokens")]
    [InlineData("cisco_ai_defense.timeout_ms")]
    public void A_timeout_interval_retry_or_maximum_must_not_be_negative(string key)
    {
        AssertError(Check(key, Int, "-1"), "value must be zero or greater");
        AssertOk(Check(key, Int, "0"));
        AssertOk(Check(key, Int, "30"));
    }

    [Theory]
    [InlineData("openshell.workdir.git_depth")]
    [InlineData("openshell.approvals.debounce_ms")]
    public void Every_openshell_integer_must_not_be_negative(string key) =>
        AssertError(Check(key, Int, "-1"), "value must be zero or greater");

    [Fact]
    public void An_integer_under_a_key_the_rules_do_not_name_may_be_negative() =>
        AssertOk(Check("agent.depth", Int, "-5"));

    // ------------------------------------------------------------------ env var names

    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("_PRIVATE")]
    [InlineData("A")]
    [InlineData("TOKEN_2")]
    [InlineData("  DEFENSECLAW_GATEWAY_TOKEN  ")]
    [InlineData("")]
    public void An_env_var_name_is_upper_case_letters_digits_and_underscores(string value) =>
        AssertOk(Check("llm.api_key_env", Env, value));

    [Theory]
    [InlineData("my_key")]
    [InlineData("MyKey")]
    [InlineData("1ABC")]
    [InlineData("MY-KEY")]
    [InlineData("MY KEY")]
    [InlineData("MY.KEY")]
    [InlineData("KEY=VALUE")]
    [InlineData("$KEY")]
    public void Anything_else_is_not_an_env_var_name(string value) =>
        AssertError(Check("gateway.token_env", Env, value), "env var names must match A-Z, 0-9, and underscores");

    [Theory]
    [InlineData("gateway.token_env")]
    [InlineData("llm.api_key_env")]
    [InlineData("guardrail.judge.api_key_env")]
    [InlineData("scanners.skill_scanner.virustotal_api_key_env")]
    public void A_text_field_whose_key_names_an_env_var_is_checked_as_one(string key) =>
        AssertError(Check(key, Text, "lower"), "env var names must match");

    [Theory]
    [InlineData("gateway.token_env", true)]
    [InlineData("llm.api_key_env", true)]
    [InlineData("a.api_key_env.b", true)]
    [InlineData("a.token_env.b", true)]
    [InlineData("gateway.environment", false)]
    [InlineData("ai_discovery.include_env_var_names", false)]
    public void An_env_key_ends_in_env_or_is_an_api_key_or_token_env(string key, bool expected) =>
        Assert.Equal(expected, ConfigFieldValidator.IsEnvNameKey(key));

    [Fact]
    public void A_bool_whose_label_would_say_env_is_not_an_env_var_name()
    {
        // The TUI also tests the label for " Env"; this form derives the label from the key ("Include Env Var Names"), which would make this a name.
        AssertOk(Check("ai_discovery.include_env_var_names", Bool, "true"));
    }

    // ------------------------------------------------------------------ a secret typed where a name belongs

    [Fact]
    public void A_secret_in_an_env_name_box_is_a_warning_that_points_at_dot_env_and_keys_set_and_never_repeats_it()
    {
        var secret = Sk();

        var result = Check("llm.api_key_env", Env, secret);

        AssertWarning(result, "this looks like a secret value, not an env var name");
        Assert.Contains("keys set", result.Message, StringComparison.Ordinal);
        Assert.Contains(".env", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.DisplayText, StringComparison.Ordinal);
    }

    public static TheoryData<string> SecretShapes { get; } = new()
    {
        Fake("s" + "k-", 20),
        Fake("gh" + "p_", 20),
        Fake("gh" + "o_", 20),
        Fake("gh" + "s_", 20),
        Fake("AI" + "za", 20),
        Fake("AK" + "IA", 20),
        Fake("AS" + "IA", 20),
        Fake("ey" + "J", 20),
        "Bearer " + new string('q', 8),
        "token bearer " + new string('q', 8),
        "-----" + "BEGIN PRIVATE KEY-----",
        new string('a', 15) + "." + new string('b', 15) + "." + new string('c', 15),
        new string('q', 32),
        "Mixed" + new string('q', 30) + "Case",
    };

    [Theory]
    [MemberData(nameof(SecretShapes))]
    public void Every_shape_the_tui_calls_a_secret_is_one(string value) =>
        Assert.True(ConfigFieldValidator.LooksLikeSecretValue(value));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("gpt-4o")]
    [InlineData("localhost")]
    [InlineData("a.b.c")]
    [InlineData("0123456789012345678901234567890")]
    public void Ordinary_text_is_not(string value) =>
        Assert.False(ConfigFieldValidator.LooksLikeSecretValue(value));

    [Fact]
    public void A_long_name_is_not_a_secret_but_a_long_word_is()
    {
        Assert.False(ConfigFieldValidator.LooksLikeSecretValue(new string('A', 40)));
        Assert.True(ConfigFieldValidator.LooksLikeSecretValue(new string('a', 32)));
        Assert.False(ConfigFieldValidator.LooksLikeSecretValue(new string('a', 31)));
    }

    [Fact]
    public void Three_short_dotted_parts_are_a_hostname_not_a_token_and_forty_four_characters_of_them_are_one()
    {
        Assert.False(ConfigFieldValidator.LooksLikeSecretValue(new string('a', 9) + "." + new string('b', 9) + "." + new string('c', 9)));
        Assert.True(ConfigFieldValidator.LooksLikeSecretValue(new string('a', 14) + "." + new string('b', 14) + "." + new string('c', 14)));
    }

    [Fact]
    public void A_short_lower_case_word_in_an_env_name_box_is_an_error_not_a_secret_warning() =>
        AssertError(Check("llm.api_key_env", Env, "hunter2"), "env var names must match");

    [Fact]
    public void A_credential_that_is_shaped_like_a_name_is_caught_by_the_inline_rule()
    {
        // An AWS access key id is upper-case letters and digits: a valid-looking name, and still a credential.
        var value = "AK" + "IA" + new string('Q', 16);

        var result = Check("llm.api_key_env", Env, value);

        AssertWarning(result, "secret-like value will be saved inline");
        Assert.DoesNotContain(value, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_named_text_field_with_a_secret_shaped_value_is_a_warning_that_says_where_secrets_go()
    {
        var value = Sk();

        var result = Check("custom.token_note", Text, value);

        AssertWarning(result, "secret-like value will be saved inline");
        Assert.Contains("keys set", result.Message, StringComparison.Ordinal);
        Assert.Contains(".env", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_value_under_an_ordinary_key_or_in_a_password_field_is_not_flagged()
    {
        AssertOk(Check("custom.note", Text, Sk()));
        AssertOk(Check("llm.api_key", FormFieldKind.Secret, Sk()));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("secret")]
    [InlineData("token")]
    [InlineData("api_key")]
    [InlineData("apikey")]
    [InlineData("access_key")]
    [InlineData("private_key")]
    public void A_secret_name_is_any_of_the_tuis_markers_in_the_key(string marker)
    {
        Assert.True(ConfigFieldValidator.IsSecretName($"x.my_{marker}_y"));
        Assert.True(ConfigFieldValidator.IsSecretName($"x.MY_{marker.ToUpperInvariant()}"));
    }

    [Fact]
    public void An_ordinary_key_is_not_a_secret_name() =>
        Assert.False(ConfigFieldValidator.IsSecretName("gateway.api_port"));

    // ------------------------------------------------------------------ URLs

    [Theory]
    [InlineData("https://api.example.test/v1")]
    [InlineData("http://localhost:11434")]
    [InlineData("grpc://collector.example.test:4317")]
    [InlineData("HTTPS://Host.Example.Test")]
    [InlineData("  https://api.example.test  ")]
    [InlineData("http://[::1]:8080/x")]
    [InlineData("http://[fe80::1%eth0]:8080")]
    [InlineData("https://@host.example.test")]
    [InlineData("https://host.example.test?x=1")]
    public void A_url_with_a_scheme_and_a_host_is_fine(string value) => AssertOk(Check("llm.base_url", Text, value));

    [Theory]
    [InlineData("localhost:11434")]
    [InlineData("api.example.test")]
    [InlineData("api.example.test/v1")]
    [InlineData("//api.example.test")]
    [InlineData("https://")]
    [InlineData("https:///path")]
    [InlineData("http:/host")]
    [InlineData("http://:8080")]
    [InlineData("http://user@:8080")]
    [InlineData("http://[::1")]
    [InlineData("http://::1]")]
    [InlineData("http://[zzzz]/")]
    [InlineData("http://[1.2.3.4]/")]
    [InlineData("1http://host")]
    [InlineData("-http://host")]
    public void Anything_without_both_is_an_error(string value) =>
        AssertError(Check("llm.base_url", Text, value), "expected a URL with scheme and host");

    [Fact]
    public void A_url_must_not_carry_credentials_and_the_message_never_repeats_them()
    {
        var user = "ex" + "ample-user";
        var pass = "ex" + "ample-pass";

        foreach (var value in new[]
                 {
                     "https://" + user + ":" + pass + "@host.example.test/v1",
                     "https://" + user + "@host.example.test",
                     "https://:" + pass + "@host.example.test",
                     "https://" + user + ":@host.example.test",
                 })
        {
            var result = Check("guardrail.api_base", Text, value);

            AssertError(result, "URL must not embed credentials");
            Assert.DoesNotContain(user, result.DisplayText, StringComparison.Ordinal);
            Assert.DoesNotContain(pass, result.DisplayText, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ftp://files.example.test")]
    [InlineData("unix://host")]
    [InlineData("tcp://host:1")]
    [InlineData("wss://host.example.test")]
    public void An_uncommon_scheme_is_a_warning(string value) =>
        AssertWarning(Check("cisco_ai_defense.endpoint", Text, value), "uncommon URL scheme");

    [Theory]
    [InlineData("llm.base_url")]
    [InlineData("guardrail.api_base")]
    [InlineData("guardrail.judge.api_base")]
    [InlineData("cisco_ai_defense.endpoint")]
    [InlineData("webhook.url")]
    public void These_keys_are_urls(string key)
    {
        Assert.True(ConfigFieldValidator.LooksLikeUrlField(key));
        AssertError(Check(key, Text, "not a url"), "expected a URL with scheme and host");
    }

    [Fact]
    public void An_empty_url_is_fine_it_means_unset() => AssertOk(Check("llm.base_url", Text, string.Empty));

    [Fact]
    public void A_key_that_is_not_a_url_is_not_checked_as_one() => AssertOk(Check("llm.model", Text, "localhost:11434"));

    [Fact]
    public void The_url_rule_is_for_text_only()
    {
        // `endpoint_timeout_ms: 5000` is a number, and `*_url_env` holds a variable NAME; neither is "expected a URL".
        AssertOk(Check("observability.endpoint_timeout_ms", Int, "5000"));
        AssertOk(Check("splunk.hec_url_env", Env, "SPLUNK_URL"));
        AssertError(Check("splunk.hec_url_env", Env, "splunk_url"), "env var names must match");
    }

    [Theory]
    [InlineData("https://h.example.test/x", "https", "h.example.test", null, null)]
    [InlineData("HTTP://H.EXAMPLE.TEST:8080", "http", "H.EXAMPLE.TEST", null, null)]
    [InlineData("https://u:p@h.example.test", "https", "h.example.test", "u", "p")]
    [InlineData("https://u@h.example.test", "https", "h.example.test", "u", null)]
    [InlineData("https://:p@h.example.test", "https", "h.example.test", "", "p")]
    [InlineData("https://a:b:c@h.example.test", "https", "h.example.test", "a", "b:c")]
    [InlineData("https://x@y@h.example.test", "https", "h.example.test", "x@y", null)]
    [InlineData("http://[::1]:80", "http", "::1", null, null)]
    [InlineData("h.example.test:80", "h.example.test", "", null, null)]
    [InlineData("mailto:someone", "mailto", "", null, null)]
    [InlineData("/just/a/path", "", "", null, null)]
    public void A_url_splits_the_way_pythons_urlparse_does(string value, string scheme, string host, string? username, string? password)
    {
        Assert.True(ConfigFieldValidator.TryParseUrl(value, out var url));

        Assert.Equal(scheme, url.Scheme);
        Assert.Equal(host, url.Host);
        Assert.Equal(username, url.Username);
        Assert.Equal(password, url.Password);
    }

    [Fact]
    public void Control_characters_before_a_url_and_tabs_inside_it_are_dropped_like_urlparse_does()
    {
        Assert.True(ConfigFieldValidator.TryParseUrl("\u0001 \thtt\tps://h.example.test", out var url));

        Assert.Equal("https", url.Scheme);
        Assert.Equal("h.example.test", url.Host);
    }

    // ------------------------------------------------------------------ OTLP endpoints (host:port)

    [Theory]
    [InlineData("otel.endpoint", true)]
    [InlineData("observability.otlp.endpoint", true)]
    [InlineData("otel.exporter.endpoint", true)]
    [InlineData("observability.destinations.local.endpoint", false)]
    [InlineData("cisco_ai_defense.endpoint", false)]
    [InlineData("otel.protocol", false)]
    [InlineData("endpoint", false)]
    public void An_otlp_endpoint_is_an_endpoint_inside_an_otel_or_otlp_block(string key, bool expected) =>
        Assert.Equal(expected, ConfigFieldValidator.IsOtlpEndpointField(key));

    [Theory]
    [InlineData("localhost:4317")]
    [InlineData("collector.example.test:4318")]
    [InlineData("[::1]:4317")]
    [InlineData("10.0.0.5:1")]
    [InlineData("host:65535")]
    public void An_otlp_endpoint_may_be_a_host_and_a_port(string value) =>
        AssertOk(Check("otel.endpoint", Text, value));

    [Theory]
    [InlineData(":4317")]
    [InlineData("localhost")]
    [InlineData("localhost:")]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData("localhost:abc")]
    [InlineData("[]:4317")]
    public void Anything_else_without_a_scheme_is_an_error_that_says_host_port_is_allowed(string value) =>
        AssertError(Check("otel.endpoint", Text, value), "expected a URL with scheme and host or host:port");

    [Fact]
    public void An_otlp_endpoint_with_a_scheme_is_checked_as_a_url()
    {
        AssertOk(Check("otel.endpoint", Text, "http://localhost:4318"));
        AssertError(Check("otel.endpoint", Text, "http://:4318"), "expected a URL with scheme and host");
        AssertError(Check("otel.endpoint", Text, "https://u:p@host.example.test"), "URL must not embed credentials");
    }

    [Fact]
    public void The_same_host_and_port_under_an_ordinary_url_key_is_not_allowed() =>
        AssertError(Check("llm.base_url", Text, "localhost:4317"), "expected a URL with scheme and host");

    [Theory]
    [InlineData("localhost:4317", true)]
    [InlineData("[::1]:4317", true)]
    [InlineData("a:1", true)]
    [InlineData("a:65535", true)]
    [InlineData("a:0", false)]
    [InlineData("a:65536", false)]
    [InlineData("a:b", false)]
    [InlineData("nohost", false)]
    [InlineData(":80", false)]
    [InlineData(" :80", false)]
    [InlineData("a: 80", true)]
    public void Host_and_port_is_a_host_a_colon_and_a_port_from_1_to_65535(string value, bool expected) =>
        Assert.Equal(expected, ConfigFieldValidator.ValidateHostPort(value));

    // ------------------------------------------------------------------ Go durations

    [Theory]
    [InlineData("30s")]
    [InlineData("1m")]
    [InlineData("500ms")]
    [InlineData("1h30m")]
    [InlineData("1.5s")]
    [InlineData("2h45m30s")]
    [InlineData("10ns")]
    [InlineData("10us")]
    [InlineData("10µs")]
    [InlineData("30")]
    [InlineData("0")]
    [InlineData(" 30s ")]
    [InlineData("")]
    public void A_dedup_window_is_a_go_duration_or_a_number_of_seconds(string value) =>
        AssertOk(Check("notifications.dedup_window", Text, value));

    [Theory]
    [InlineData("30 s")]
    [InlineData("1d")]
    [InlineData("abc")]
    [InlineData("s")]
    [InlineData("-5s")]
    [InlineData(".5s")]
    [InlineData("5.s")]
    [InlineData("1m 30s")]
    [InlineData("1h-30m")]
    [InlineData("5S")]
    [InlineData("5μs")]
    public void Anything_else_is_not_a_duration(string value) =>
        AssertError(Check("notifications.dedup_window", Text, value), "duration must be like 30s, 1m, or a seconds integer");

    [Fact]
    public void A_dedup_window_written_as_a_number_is_checked_as_seconds()
    {
        AssertOk(Check("notifications.dedup_window", Int, "30"));
        AssertError(Check("notifications.dedup_window", Int, "-5"), "duration must be like");
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("12", true)]
    [InlineData("12s", true)]
    [InlineData("1h2m3s4ms", true)]
    [InlineData("12x", false)]
    public void The_duration_helper_reads_empty_digits_and_unit_pairs(string value, bool expected) =>
        Assert.Equal(expected, ConfigFieldValidator.LooksLikeGoDurationOrSeconds(value));

    // ------------------------------------------------------------------ TLS

    [Fact]
    public void Turning_certificate_verification_off_is_a_warning_and_leaving_it_on_is_not()
    {
        AssertWarning(Check("gateway.tls_skip_verify", Bool, "true"), "TLS verification is disabled; dev-only");
        AssertOk(Check("gateway.tls_skip_verify", Bool, "false"));
        AssertWarning(Check("llm.tls_skip_verify", Bool, "true"), "TLS verification is disabled");
    }

    // ------------------------------------------------------------------ what the result says, and who gets blocked

    [Fact]
    public void A_result_names_its_severity_in_words()
    {
        Assert.Equal("Error: port must be between 1 and 65535", Check("gateway.api_port", Int, "70000").DisplayText);
        Assert.StartsWith("Warning: ", Check("gateway.tls_skip_verify", Bool, "true").DisplayText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, FieldValidation.Ok.DisplayText);
    }

    [Fact]
    public void A_value_that_is_already_in_the_file_never_blocks_its_error_becomes_a_warning_that_says_so()
    {
        var error = Check("gateway.api_port", Int, "70000");

        var existing = error.AsExistingValue();

        AssertWarning(existing, "already in config.yaml, kept as is: port must be between 1 and 65535");
        Assert.False(existing.IsError);
    }

    [Fact]
    public void Existing_values_that_were_only_warnings_or_fine_are_unchanged()
    {
        var warning = Check("gateway.tls_skip_verify", Bool, "true");

        Assert.Same(warning, warning.AsExistingValue());
        Assert.Same(FieldValidation.Ok, FieldValidation.Ok.AsExistingValue());
    }

    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("  OPENAI_API_KEY  ")]
    [InlineData("_X")]
    [InlineData("A\n")]
    public void The_env_name_helper_reads_the_trimmed_text(string value) =>
        Assert.True(ConfigFieldValidator.LooksLikeEnvName(value));

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("A-B")]
    [InlineData("A B")]
    [InlineData("A\nB")]
    public void The_env_name_helper_wants_the_whole_text_to_match(string value) =>
        Assert.False(ConfigFieldValidator.LooksLikeEnvName(value));
}
