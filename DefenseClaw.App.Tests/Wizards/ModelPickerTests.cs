using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The searchable model picker's rules and its catalogue (CUST-269). The rules are the TUI's (<c>filter_models</c> and <c>picker_rows</c> in
/// <c>tui/screens/model_picker.py</c>, 0.8.10); the catalogue is the JSON file the runtime ships (<c>_data/llm/model_catalog.json</c>), which the tests
/// replace with a synthetic one of the same shape. Nothing here asks a provider anything.
/// </summary>
public sealed class ModelPickerTests : IDisposable
{
    private static readonly string[] Models = { "acme-large-2", "acme-large-1", "acme-small-1", "acme-mini", "orbit-5" };

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "llm", name));

    private static ModelCatalogue Synthetic(bool withInstances = false) =>
        ModelCatalogue.Parse(
            Fixture("model-catalog.synthetic.json"),
            withInstances ? ModelCatalogue.ParseInstances(Fixture("custom-providers.synthetic.json")) : null)!;

    // ------------------------------------------------------------------ filtering

    [Fact]
    public void An_empty_query_lists_every_model_in_the_catalogues_order()
    {
        Assert.Equal(Models, ModelPicker.Filter(string.Empty, Models));
        Assert.Equal(Models, ModelPicker.Filter(null, Models));
        Assert.Equal(Models, ModelPicker.Filter("   ", Models));
    }

    [Fact]
    public void Matches_are_ranked_exact_then_prefix_then_contains_and_keep_the_catalogues_order_within_a_rank()
    {
        var models = new[] { "x-acme", "acme-large-1", "acme", "big-acme-x", "acme-mini" };

        var found = ModelPicker.Filter("acme", models);

        // exact "acme"; then prefixes in catalogue order; then contains, the earlier hit first ("x-acme" at 2 before "big-acme-x" at 4).
        Assert.Equal(new[] { "acme", "acme-large-1", "acme-mini", "x-acme", "big-acme-x" }, found);
    }

    [Fact]
    public void Matching_ignores_case_and_surrounding_spaces_and_leaves_out_what_does_not_contain_the_query()
    {
        Assert.Equal(new[] { "acme-large-2", "acme-large-1" }, ModelPicker.Filter("  ACME-LARGE ", Models));
        Assert.Empty(ModelPicker.Filter("nothing-like-it", Models));
    }

    // ------------------------------------------------------------------ the "use as typed" row

    [Fact]
    public void Typed_text_that_is_not_a_model_is_the_first_row_marked_as_typed()
    {
        var rows = ModelPicker.Rows("acme-large-9", Models);

        Assert.Equal("acme-large-9", rows[0].Value);
        Assert.True(rows[0].IsTyped);
        Assert.Equal("Use “acme-large-9” as typed", rows[0].Label);
        Assert.Contains("not in the catalogue", rows[0].AutomationName, StringComparison.Ordinal);

        // The models that contain what was typed follow ("acme-large-9" matches none).
        Assert.Single(rows);
    }

    [Fact]
    public void The_typed_row_comes_first_in_front_of_the_models_that_match_it()
    {
        var rows = ModelPicker.Rows("acme-la", Models);

        Assert.Equal(new[] { "acme-la", "acme-large-2", "acme-large-1" }, rows.Select(r => r.Value).ToArray());
        Assert.Equal(new[] { true, false, false }, rows.Select(r => r.IsTyped).ToArray());
    }

    [Fact]
    public void Typed_text_that_is_a_model_exactly_is_not_offered_twice()
    {
        var rows = ModelPicker.Rows("acme-mini", Models);

        Assert.Equal(new[] { "acme-mini" }, rows.Select(r => r.Value).ToArray());
        Assert.False(rows[0].IsTyped);
    }

    [Fact]
    public void Typed_text_is_compared_with_its_case_so_a_different_case_is_a_row_of_its_own()
    {
        var rows = ModelPicker.Rows("ACME-MINI", Models);

        Assert.Equal(new[] { "ACME-MINI", "acme-mini" }, rows.Select(r => r.Value).ToArray());
        Assert.True(rows[0].IsTyped);
    }

    [Fact]
    public void Nothing_typed_is_no_typed_row_and_text_with_a_bracket_is_just_text()
    {
        Assert.DoesNotContain(ModelPicker.Rows(string.Empty, Models), r => r.IsTyped);
        Assert.All(ModelPicker.Rows(string.Empty, Models), r => Assert.False(r.IsTyped));

        // The TUI had to escape this for its markup renderer; here it is only text.
        var rows = ModelPicker.Rows("gpt[4", Models);
        Assert.Equal("gpt[4", rows[0].Value);
        Assert.True(rows[0].IsTyped);
    }

    [Fact]
    public void With_no_models_at_all_the_typed_text_is_still_a_row()
    {
        var rows = ModelPicker.Rows("local-llama", Array.Empty<string>());

        Assert.Equal(new[] { "local-llama" }, rows.Select(r => r.Value).ToArray());
        Assert.True(rows[0].IsTyped);
        Assert.Empty(ModelPicker.Rows(string.Empty, Array.Empty<string>()));
    }

    // ------------------------------------------------------------------ the catalogue

    [Fact]
    public void The_catalogue_lists_a_providers_models_in_its_order_and_matches_the_provider_without_regard_to_case()
    {
        var catalogue = Synthetic();

        Assert.Equal(new[] { "acme-large-2", "acme-large-1", "acme-small-1", "acme-mini" }, catalogue.ModelsFor("anthropic"));
        Assert.Equal(new[] { "acme-large-2", "acme-large-1", "acme-small-1", "acme-mini" }, catalogue.ModelsFor(" Anthropic "));
        Assert.Equal("AWS Bedrock", catalogue.LabelFor("bedrock"));
        Assert.Equal(5, catalogue.Providers.Count);
    }

    [Fact]
    public void A_provider_with_no_list_a_provider_the_catalogue_lacks_and_no_provider_have_no_models()
    {
        var catalogue = Synthetic();

        Assert.Empty(catalogue.ModelsFor("vllm"));
        Assert.Empty(catalogue.ModelsFor("a-provider-nobody-shipped"));
        Assert.Empty(catalogue.ModelsFor(string.Empty));
        Assert.Empty(catalogue.ModelsFor(null));
        Assert.Null(catalogue.LabelFor("a-provider-nobody-shipped"));
    }

    [Fact]
    public void A_custom_instance_with_models_of_its_own_wins_over_its_providers_list()
    {
        var catalogue = Synthetic(withInstances: true);

        Assert.Equal(new[] { "corp-model-a", "corp-model-b" }, catalogue.ModelsFor("openai", "corp-gateway"));
        Assert.Equal(new[] { "corp-model-a", "corp-model-b" }, catalogue.ModelsFor("custom", "CORP-GATEWAY"));

        // An instance with no list, or one that is not registered, leaves the provider's.
        Assert.Equal(new[] { "orbit-5", "orbit-5-mini", "orbit-4" }, catalogue.ModelsFor("openai", "empty-one"));
        Assert.Equal(new[] { "orbit-5", "orbit-5-mini", "orbit-4" }, catalogue.ModelsFor("openai", "never-registered"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"providers\": 7}")]
    [InlineData("{\"providers\": []}")]
    [InlineData("{\"providers\": [{\"label\": \"no name\"}, 3, null]}")]
    public void Text_that_is_not_a_catalogue_is_no_catalogue(string text)
    {
        Assert.Null(ModelCatalogue.Parse(text));
    }

    [Fact]
    public void Models_that_could_not_be_typed_or_shown_on_one_row_are_left_out()
    {
        var long200 = new string('m', 201);
        var json = "{\"providers\":[{\"name\":\"p\",\"models\":[\"ok-1\",\"ok-1\",\"\",\"  \",\"bad\\nid\",\"" + long200 + "\",7,\"ok-2\"]}]}";

        var catalogue = ModelCatalogue.Parse(json)!;

        Assert.Equal(new[] { "ok-1", "ok-2" }, catalogue.ModelsFor("p"));
    }

    [Fact]
    public void Instances_come_from_the_overlay_and_a_broken_overlay_is_no_instances()
    {
        var instances = ModelCatalogue.ParseInstances(Fixture("custom-providers.synthetic.json"));

        Assert.Equal(new[] { "corp-model-a", "corp-model-b" }, instances["corp-gateway"]);
        Assert.Empty(instances["empty-one"]);
        Assert.Empty(ModelCatalogue.ParseInstances("{ broken"));
        Assert.Empty(ModelCatalogue.ParseInstances("[]"));
    }

    // ------------------------------------------------------------------ finding the file beside the CLI

    private string Install(string layout, string? catalogueText = null)
    {
        var root = Path.Combine(_temp.Path, "DefenseClaw");
        var bin = Path.Combine(root, layout == "venv" ? "Scripts" : "bin");
        _ = Directory.CreateDirectory(bin);
        var cli = Path.Combine(bin, "defenseclaw.exe");
        File.WriteAllText(cli, string.Empty);

        var folder = layout == "venv"
            ? Path.Combine(root, "Lib", "site-packages", "defenseclaw", "_data", "llm")
            : Path.Combine(root, "runtime", "python", "Lib", "site-packages", "defenseclaw", "_data", "llm");
        _ = Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "model_catalog.json"), catalogueText ?? Fixture("model-catalog.synthetic.json"));
        return cli;
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("venv")]
    public void The_catalogue_is_found_beside_the_cli_in_either_layout_and_read(string layout)
    {
        var cli = Install(layout);

        Assert.NotNull(ModelCatalogue.Locate(cli));
        var catalogue = ModelCatalogue.Load(cli, _temp.Path);

        Assert.NotNull(catalogue);
        Assert.Equal(ModelCatalogue.Locate(cli), catalogue!.Source);
        Assert.Equal(new[] { "orbit-5", "orbit-5-mini", "orbit-4" }, catalogue.ModelsFor("openai"));
    }

    [Fact]
    public void The_overlay_in_the_data_folder_is_read_with_it()
    {
        var cli = Install("setup");
        File.WriteAllText(Path.Combine(_temp.Path, "custom-providers.json"), Fixture("custom-providers.synthetic.json"));

        var catalogue = ModelCatalogue.Load(cli, _temp.Path)!;

        Assert.Equal(new[] { "corp-model-a", "corp-model-b" }, catalogue.ModelsFor("custom", "corp-gateway"));
    }

    [Fact]
    public void A_runtime_laid_out_some_other_way_has_no_catalogue()
    {
        var elsewhere = Path.Combine(_temp.Path, "Elsewhere", "bin");
        _ = Directory.CreateDirectory(elsewhere);
        var cli = Path.Combine(elsewhere, "defenseclaw.exe");
        File.WriteAllText(cli, string.Empty);

        Assert.Null(ModelCatalogue.Locate(cli));
        Assert.Null(ModelCatalogue.Load(cli, _temp.Path));
        Assert.Null(ModelCatalogue.Locate(null));
        Assert.Null(ModelCatalogue.Locate("   "));
        Assert.Null(ModelCatalogue.Locate("defenseclaw.exe"));
    }

    [Fact]
    public void A_catalogue_file_that_is_broken_or_too_big_is_no_catalogue_and_never_throws()
    {
        Assert.Null(ModelCatalogue.Load(Install("setup", "{ this is not json"), _temp.Path));

        using var other = new TempDirectory();
        var root = Path.Combine(other.Path, "DefenseClaw");
        var folder = Path.Combine(root, "runtime", "python", "Lib", "site-packages", "defenseclaw", "_data", "llm");
        _ = Directory.CreateDirectory(folder);
        _ = Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(
            Path.Combine(folder, "model_catalog.json"),
            "{\"providers\":[{\"name\":\"p\",\"models\":[\"" + new string('x', (int)ModelCatalogue.MaxFileBytes) + "\"]}]}");

        Assert.Null(ModelCatalogue.Load(Path.Combine(root, "bin", "defenseclaw.exe"), other.Path));
    }

    // ------------------------------------------------------------------ the model box (a field)

    private static WizardFieldViewModel ModelBox(WizardValues values, string providerId = "provider", string instanceId = "instance-name")
    {
        var field = new WizardField
        {
            Id = "model",
            Label = "Model",
            Kind = WizardFieldKind.Text,
            Flag = "--model",
        }.WithPicker(WizardFieldPickers.Model, providerId, instanceId);

        return new WizardFieldViewModel(field, values);
    }

    [Fact]
    public void Without_a_catalogue_the_model_field_is_the_plain_text_box_it_always_was()
    {
        var box = ModelBox(new WizardValues());

        Assert.True(box.IsModelPicker);
        Assert.False(box.ShowsModelPicker);
        Assert.True(box.IsPlainText);
        Assert.Empty(box.ModelRows);

        // Opening is refused, and the text is the answer.
        box.OpenModelList();
        Assert.False(box.IsModelListOpen);
        box.Value = "anything-typed";
        Assert.Empty(box.ModelRows);
        Assert.Equal("anything-typed", box.Value);
    }

    [Fact]
    public void With_a_catalogue_the_box_lists_the_providers_models_and_narrows_them_as_the_operator_types()
    {
        var values = new WizardValues();
        values["provider"] = "anthropic";
        var box = ModelBox(values);

        box.AttachCatalogue(Synthetic());
        Assert.True(box.ShowsModelPicker);
        Assert.False(box.IsPlainText);
        Assert.Equal(new[] { "acme-large-2", "acme-large-1", "acme-small-1", "acme-mini" }, box.ModelRows.Select(r => r.Value).ToArray());
        Assert.Contains("4 model(s)", box.ModelListNote, StringComparison.Ordinal);
        Assert.Contains("Anthropic (Claude)", box.ModelListNote, StringComparison.Ordinal);

        box.Value = "small";
        Assert.Equal(new[] { "small", "acme-small-1" }, box.ModelRows.Select(r => r.Value).ToArray());
        Assert.True(box.ModelRows[0].IsTyped);
        Assert.Equal(box.ModelRows[0], box.SelectedModelRow);
    }

    [Fact]
    public void Changing_the_provider_lists_the_new_providers_models()
    {
        var values = new WizardValues();
        values["provider"] = "anthropic";
        var box = ModelBox(values);
        box.AttachCatalogue(Synthetic());

        values["provider"] = "bedrock";
        box.RefreshModelRows();

        Assert.Equal(new[] { "us.acme.large-v1:0", "us.acme.small-v1:0", "eu.acme.large-v1:0" }, box.ModelRows.Select(r => r.Value).ToArray());

        values["provider"] = "vllm";
        box.RefreshModelRows();
        Assert.Empty(box.ModelRows);
        Assert.Contains("lists no models", box.ModelListNote, StringComparison.Ordinal);

        values["provider"] = string.Empty;
        box.RefreshModelRows();
        Assert.Contains("Choose a provider", box.ModelListNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_instance_is_listed_by_its_own_models()
    {
        var values = new WizardValues();
        values["provider"] = "custom";
        values["instance-name"] = "corp-gateway";
        var box = ModelBox(values);

        box.AttachCatalogue(Synthetic(withInstances: true));

        Assert.Equal(new[] { "corp-model-a", "corp-model-b" }, box.ModelRows.Select(r => r.Value).ToArray());
        Assert.Contains("instance corp-gateway", box.ModelListNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_a_row_puts_it_in_the_box_and_closes_the_list_and_the_typed_row_keeps_the_text()
    {
        var values = new WizardValues();
        values["provider"] = "openai";
        var box = ModelBox(values);
        box.AttachCatalogue(Synthetic());
        box.OpenModelList();
        Assert.True(box.IsModelListOpen);

        box.ChooseModelCommand.Execute(box.ModelRows.First(r => r.Value == "orbit-5-mini"));
        Assert.Equal("orbit-5-mini", box.Value);
        Assert.Equal("orbit-5-mini", values["model"]);
        Assert.False(box.IsModelListOpen);

        box.Value = "orbit-9";
        box.OpenModelList();
        var typed = box.ModelRows[0];
        Assert.True(typed.IsTyped);
        box.ChooseModelCommand.Execute(typed);
        Assert.Equal("orbit-9", box.Value);
        Assert.False(box.IsModelListOpen);
    }

    [Fact]
    public void The_arrow_keys_move_the_highlight_around_the_list_and_enter_takes_it()
    {
        var values = new WizardValues();
        values["provider"] = "openai";
        var box = ModelBox(values);
        box.AttachCatalogue(Synthetic());

        // Closed: the first arrow opens the list; it does not move.
        box.MoveModelSelection(1);
        Assert.True(box.IsModelListOpen);
        Assert.Equal("orbit-5", box.SelectedModelRow!.Value);

        box.MoveModelSelection(1);
        Assert.Equal("orbit-5-mini", box.SelectedModelRow!.Value);
        box.MoveModelSelection(1);
        box.MoveModelSelection(1);
        Assert.Equal("orbit-5", box.SelectedModelRow!.Value); // wrapped, as the TUI's picker does
        box.MoveModelSelection(-1);
        Assert.Equal("orbit-4", box.SelectedModelRow!.Value);

        Assert.True(box.AcceptModelSelection());
        Assert.Equal("orbit-4", box.Value);
        Assert.False(box.IsModelListOpen);

        // Enter with the list shut is not the picker's: it means what it always did.
        Assert.False(box.AcceptModelSelection());
    }

    [Fact]
    public void Closing_the_list_leaves_the_text_alone_and_losing_the_catalogue_goes_back_to_a_text_box()
    {
        var values = new WizardValues();
        values["provider"] = "openai";
        var box = ModelBox(values);
        box.AttachCatalogue(Synthetic());
        box.Value = "orb";
        box.OpenModelList();

        box.CloseModelList();
        Assert.False(box.IsModelListOpen);
        Assert.Equal("orb", box.Value);

        box.OpenModelList();
        box.AttachCatalogue(null);
        Assert.False(box.ShowsModelPicker);
        Assert.True(box.IsPlainText);
        Assert.False(box.IsModelListOpen);
        Assert.Empty(box.ModelRows);
        Assert.Equal("orb", box.Value);
    }

    [Fact]
    public void A_field_that_is_not_a_model_field_never_shows_the_picker()
    {
        var field = new WizardField { Id = "timeout", Label = "Timeout", Kind = WizardFieldKind.Integer, Flag = "--timeout" };
        var box = new WizardFieldViewModel(field, new WizardValues());

        box.AttachCatalogue(Synthetic());

        Assert.False(box.IsModelPicker);
        Assert.False(box.ShowsModelPicker);
        Assert.True(box.IsPlainText);
    }
}
