using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The language a page or a field says when it is shown in (CUST-269): the plain gate every wizard has always used, any-of over fields, and the
/// compound AND the provider groups and the guardrail's scope are built from.
/// </summary>
public class WizardGateTests
{
    private static WizardValues Values(params (string Id, string Value)[] answers)
    {
        var values = new WizardValues();
        foreach (var (id, value) in answers)
        {
            values[id] = value;
        }

        return values;
    }

    [Fact]
    public void No_gate_is_always_shown()
    {
        Assert.True(WizardGate.IsVisible(null, Array.Empty<string>(), Values()));
        Assert.True(WizardGate.IsVisible(string.Empty, Array.Empty<string>(), Values()));
    }

    [Fact]
    public void A_plain_gate_is_shown_when_the_field_holds_one_of_the_values()
    {
        var ids = new[] { "bedrock", "azure" };

        Assert.True(WizardGate.IsVisible("provider", ids, Values(("provider", "azure"))));
        Assert.False(WizardGate.IsVisible("provider", ids, Values(("provider", "openai"))));
        Assert.False(WizardGate.IsVisible("provider", ids, Values()));

        // Values are compared exactly: the gate reads what the control holds.
        Assert.False(WizardGate.IsVisible("provider", ids, Values(("provider", "Azure"))));
    }

    [Fact]
    public void A_gate_on_the_empty_value_is_shown_while_the_field_is_blank_or_missing()
    {
        // The splunk group's guided pages are gated on the subcommand being empty; a wizard with no such field answers "".
        Assert.True(WizardGate.IsVisible("subcommand", new[] { string.Empty }, Values()));
        Assert.True(WizardGate.IsVisible("subcommand", new[] { string.Empty }, Values(("subcommand", string.Empty))));
        Assert.False(WizardGate.IsVisible("subcommand", new[] { string.Empty }, Values(("subcommand", "dashboards"))));
    }

    [Fact]
    public void An_any_of_gate_is_shown_when_any_of_the_fields_holds_a_value()
    {
        var on = new[] { ToggleValues.On };

        Assert.True(WizardGate.IsVisible("logs|enterprise", on, Values(("logs", ToggleValues.On))));
        Assert.True(WizardGate.IsVisible("logs|enterprise", on, Values(("enterprise", ToggleValues.On))));
        Assert.False(WizardGate.IsVisible("logs|enterprise", on, Values(("logs", ToggleValues.Off), ("enterprise", ToggleValues.Off))));
    }

    [Fact]
    public void A_compound_gate_needs_every_clause()
    {
        var (id, values) = WizardGate.All(
            WizardGate.When("scope", "global-all-active"),
            WizardGate.When("detection-strategy", "regex_judge", "judge_first"),
            WizardGate.When("disable", "false", string.Empty));

        Assert.True(WizardGate.IsVisible(id, values, Values(("scope", "global-all-active"), ("detection-strategy", "judge_first"), ("disable", "false"))));

        // Each condition on its own is enough to close it.
        Assert.False(WizardGate.IsVisible(id, values, Values(("scope", "selected-connector"), ("detection-strategy", "judge_first"), ("disable", "false"))));
        Assert.False(WizardGate.IsVisible(id, values, Values(("scope", "global-all-active"), ("detection-strategy", "regex_only"), ("disable", "false"))));
        Assert.False(WizardGate.IsVisible(id, values, Values(("scope", "global-all-active"), ("detection-strategy", "judge_first"), ("disable", "true"))));
    }

    [Fact]
    public void A_compound_clause_that_allows_the_empty_value_accepts_a_field_that_is_not_there()
    {
        var (id, values) = WizardGate.All(WizardGate.When("scope", "global-all-active"), WizardGate.When("disable", "false", string.Empty));

        // A CLI with no --disable has no such field, and a page gated on "not disabled" must still show.
        Assert.True(WizardGate.IsVisible(id, values, Values(("scope", "global-all-active"))));
    }

    [Fact]
    public void One_clause_on_one_field_stays_the_plain_form_so_the_data_reads_as_it_always_did()
    {
        var (id, values) = WizardGate.All(WizardGate.When("provider", "bedrock"));

        Assert.Equal("provider", id);
        Assert.Equal(new[] { "bedrock" }, values);
    }

    [Fact]
    public void And_adds_conditions_to_a_plain_gate_a_compound_gate_and_to_no_gate()
    {
        var (plainId, plainValues) = WizardGate.And("provider", new[] { "bedrock" }, WizardGate.When("scope", "global-all-active"));
        Assert.True(WizardGate.IsVisible(plainId, plainValues, Values(("provider", "bedrock"), ("scope", "global-all-active"))));
        Assert.False(WizardGate.IsVisible(plainId, plainValues, Values(("provider", "bedrock"), ("scope", "selected-connector"))));
        Assert.False(WizardGate.IsVisible(plainId, plainValues, Values(("provider", "azure"), ("scope", "global-all-active"))));

        var (twiceId, twiceValues) = WizardGate.And(plainId, plainValues, WizardGate.When("disable", "false", string.Empty));
        Assert.True(WizardGate.IsVisible(twiceId, twiceValues, Values(("provider", "bedrock"), ("scope", "global-all-active"), ("disable", "false"))));
        Assert.False(WizardGate.IsVisible(twiceId, twiceValues, Values(("provider", "bedrock"), ("scope", "global-all-active"), ("disable", "true"))));

        var (freshId, freshValues) = WizardGate.And(null, Array.Empty<string>(), WizardGate.When("scope", "selected-connector"));
        Assert.Equal("scope", freshId);
        Assert.Equal(new[] { "selected-connector" }, freshValues);

        var (sameId, _) = WizardGate.And("provider", new[] { "bedrock" });
        Assert.Equal("provider", sameId);
    }

    [Fact]
    public void An_any_of_clause_inside_a_compound_gate_still_means_any_of()
    {
        var (id, values) = WizardGate.All(WizardGate.When("logs|enterprise", ToggleValues.On), WizardGate.When("subcommand", string.Empty));

        Assert.True(WizardGate.IsVisible(id, values, Values(("enterprise", ToggleValues.On))));
        Assert.False(WizardGate.IsVisible(id, values, Values(("enterprise", ToggleValues.On), ("subcommand", "dashboards"))));
        Assert.False(WizardGate.IsVisible(id, values, Values()));
    }

    [Theory]
    [InlineData("a=b")]
    [InlineData("a&b")]
    [InlineData("a,b")]
    public void A_value_that_holds_a_character_the_notation_uses_is_refused(string value)
    {
        Assert.Throws<ArgumentException>(() => WizardGate.When("field", value));
    }

    [Fact]
    public void A_field_id_may_hold_the_any_of_bar_and_a_colon_but_not_the_rest()
    {
        _ = WizardGate.When("add:base-provider-type", "bedrock");
        _ = WizardGate.When("a|b", "x");

        Assert.Throws<ArgumentException>(() => WizardGate.When("a=b", "x"));
        Assert.Throws<ArgumentException>(() => WizardGate.When("a&b", "x"));
        Assert.Throws<ArgumentException>(() => WizardGate.When("a,b", "x"));
        Assert.Throws<ArgumentException>(() => WizardGate.When("a", Array.Empty<string>()));
    }

    [Fact]
    public void A_malformed_compound_gate_fails_closed()
    {
        // No field to look at in the second clause: the page is not shown, rather than shown on a condition nobody can read.
        Assert.False(WizardGate.IsVisible("scope=global-all-active&=x", Array.Empty<string>(), Values(("scope", "global-all-active"))));
        Assert.False(WizardGate.IsVisible("scope=global-all-active&nofield", Array.Empty<string>(), Values(("scope", "global-all-active"))));
    }

    [Fact]
    public void The_fields_a_gate_looks_at_are_listed_for_a_page_that_wants_to_know_what_it_depends_on()
    {
        var (id, _) = WizardGate.All(WizardGate.When("logs|enterprise", "true"), WizardGate.When("scope", "x"));

        Assert.Equal(new[] { "logs", "enterprise", "scope" }, WizardGate.FieldsOf(id));
        Assert.Equal(new[] { "provider" }, WizardGate.FieldsOf("provider"));
        Assert.Empty(WizardGate.FieldsOf(null));
    }

    [Fact]
    public void The_definition_evaluates_a_page_and_a_field_gate_together_as_an_and()
    {
        // The Bedrock page is shown for the Bedrock provider; its IAM fields only for the IAM auth mode - and only on that page.
        var definition = new WizardDefinition
        {
            Target = "sample",
            Title = "Sample",
            Group = WizardGroups.Other,
            Steps = new[]
            {
                new WizardStep
                {
                    Id = "choose",
                    Title = "Choose",
                    Fields = new[]
                    {
                        new WizardField { Id = "provider", Label = "Provider", Kind = WizardFieldKind.Text },
                    },
                },
                new WizardStep
                {
                    Id = "bedrock",
                    Title = "Bedrock",
                    VisibleWhenFieldId = "provider",
                    VisibleWhenValues = new[] { "bedrock" },
                    Fields = new[]
                    {
                        new WizardField { Id = "auth", Label = "Auth", Kind = WizardFieldKind.Text },
                        new WizardField
                        {
                            Id = "iam",
                            Label = "IAM",
                            Kind = WizardFieldKind.Text,
                            VisibleWhenFieldId = "auth",
                            VisibleWhenValues = new[] { "iam_credentials" },
                        },
                    },
                },
            },
        };

        var visible = Values(("provider", "bedrock"), ("auth", "iam_credentials"));
        Assert.Equal(new[] { "provider", "auth", "iam" }, definition.VisibleFields(visible).Select(f => f.Id).ToArray());

        // The auth mode is still iam_credentials in the answers after the provider changed: the page's gate keeps its fields out.
        var stale = Values(("provider", "azure"), ("auth", "iam_credentials"));
        Assert.Equal(new[] { "provider" }, definition.VisibleFields(stale).Select(f => f.Id).ToArray());

        var evaluation = definition.Evaluate(stale);
        Assert.False(evaluation.IsShown(definition.Steps[1]));
        Assert.True(evaluation.IsShown(definition.Steps[0]));
    }
}
