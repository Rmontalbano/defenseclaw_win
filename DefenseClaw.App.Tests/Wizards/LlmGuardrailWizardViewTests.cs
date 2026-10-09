using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The LLM and guardrail wizards as the real window's content, offscreen (the window is never shown; CUST-269): the goal page, the provider-gated
/// Bedrock rows, the model picker with its list open, and the guardrail's Scope step. A PNG of each is written when <c>DC_RENDER_DIR</c> names a
/// folder, and never otherwise. The model catalogue is the synthetic one; no Docker is looked at.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LlmGuardrailWizardViewTests
{
    private const string BedrockConfig = """
        llm:
          provider: bedrock
          model: us.acme.large-v1:0
          api_key_env: AWS_BEARER_TOKEN_BEDROCK
          bedrock:
            region: us-east-1
            auth_mode: iam_credentials
            access_key_env: AWS_ACCESS_KEY_ID
            secret_key_env: AWS_SECRET_ACCESS_KEY
        """;

    private const string FleetConfig = """
        guardrail:
          enabled: true
          connector: claudecode
          mode: observe
          detection_strategy: regex_only
          connectors:
            claudecode:
              mode: action
            codex:
              mode: observe
        """;

    private sealed class NeverDocker : IDockerProbe
    {
        public Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DockerStatus(DockerState.Unknown, "Docker was not looked at.", Array.Empty<string>()));
    }

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        public Scene(WizardDefinition definition, string config, ModelCatalogue? catalogue)
        {
            _services = TestServices.Create(_temp, LineEndings.Normalize(config));
            (ViewModel, Host) = UiThread.Run(() =>
            {
                var viewModel = new WizardViewModel(_services, definition, new NeverDocker(), (_, _) => catalogue);
                var window = new WizardWindow(viewModel);
                var content = (Grid)window.Content;
                window.Content = null;
                foreach (var bar in content.Children.OfType<Wpf.Ui.Controls.TitleBar>().ToArray())
                {
                    content.Children.Remove(bar);
                }

                content.DataContext = viewModel;
                return (viewModel, new OffscreenHost(content, 900, 900));
            });

            if (catalogue is not null)
            {
                UiThread.WaitFor(() => ViewModel.Catalogue is not null, "the model catalogue");
            }
        }

        public WizardViewModel ViewModel { get; }

        public OffscreenHost Host { get; }

        public string[] VisibleTexts() =>
            VisualTree.Descendants<TextBlock>(Host.Content).Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text).ToArray();

        public void GoTo(string title)
        {
            for (var i = 0; i < 20 && ViewModel.PageTitle != title; i++)
            {
                Assert.False(ViewModel.IsReview, "ran past " + title);
                ViewModel.Next();
                Assert.False(ViewModel.HasValidationSummary, ViewModel.ValidationSummary);
            }

            Assert.Equal(title, ViewModel.PageTitle);
            Host.Relayout();
        }

        public void Dispose()
        {
            UiThread.Run(() =>
            {
                ViewModel.Dispose();
                Host.Dispose();
            });
            _services.Dispose();
            _temp.Dispose();
        }
    }

    private static ModelCatalogue Synthetic() =>
        ModelCatalogue.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "llm", "model-catalog.synthetic.json")))!;

    // ------------------------------------------------------------------ the question, and the answers

    [Fact]
    public async Task The_wizard_opens_on_what_do_you_want_to_do_with_a_radio_button_for_each_goal_and_a_line_on_where_things_stand()
    {
        using var scene = new Scene(await CatalogHelp.RealAsync("llm"), BedrockConfig, Synthetic());

        UiThread.Run(() =>
        {
            scene.Host.Relayout();
            var texts = scene.VisibleTexts();

            Assert.Equal("What do you want to do?", scene.ViewModel.PageTitle);
            Assert.Contains("Set up or change my main model", texts);
            Assert.Contains("Add or change the judge LLM", texts);
            Assert.Contains("Test my LLM connection", texts);
            Assert.Contains("Advanced: show all settings", texts);
            Assert.Contains(texts, t => t.StartsWith("Main: bedrock/us.acme.large-v1:0", StringComparison.Ordinal));

            var radios = VisualTree.Descendants<RadioButton>(scene.Host.Content).Where(r => r.IsVisible).ToArray();
            Assert.Equal(6, radios.Length);
            Assert.All(radios, r => Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(r))));
            Assert.DoesNotContain(radios, r => r.IsChecked == true);

            // The question has no flag to show: it is not an option.
            Assert.DoesNotContain(texts, t => t.StartsWith("(positional)", StringComparison.Ordinal));
            RenderTo.Png(scene.Host, "llm-goals");
        });
    }

    // ------------------------------------------------------------------ the llm wizard on bedrock

    [Fact]
    public async Task The_llm_wizard_on_bedrock_shows_the_bedrock_rows_and_no_other_clouds()
    {
        using var scene = new Scene(await CatalogHelp.RealAsync("llm"), BedrockConfig, Synthetic());

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.SelectGoal("regional"));
            scene.GoTo("AWS Bedrock");
            var texts = scene.VisibleTexts();

            // The stored provider is Bedrock, so its page follows; the sign-in rows that belong to the IAM auth mode are there.
            Assert.Contains("Bedrock region", texts);
            Assert.Contains("Bedrock auth mode", texts);
            Assert.Contains("Bedrock access key env", texts);
            Assert.Contains("Bedrock secret key env", texts);
            Assert.DoesNotContain("Bedrock profile name", texts);
            Assert.DoesNotContain(texts, t => t.StartsWith("Vertex", StringComparison.Ordinal) || t.StartsWith("Azure", StringComparison.Ordinal));

            // The stored answers are in the boxes, and read "from config.yaml".
            var boxes = VisualTree.Descendants<Wpf.Ui.Controls.TextBox>(scene.Host.Content).Where(b => b.IsVisible).Select(b => b.Text).ToArray();
            Assert.Contains("us-east-1", boxes);
            Assert.Contains("AWS_ACCESS_KEY_ID", boxes);
            Assert.Contains(texts, t => t == "from config.yaml");
            Assert.Equal("Step 3 of 5", scene.ViewModel.ProgressText);

            RenderTo.Png(scene.Host, "llm-wizard-bedrock");
        });
    }

    [Fact]
    public async Task The_model_box_is_a_searchable_list_when_the_runtime_has_a_catalogue_and_a_text_box_when_it_has_none()
    {
        using var withCatalogue = new Scene(await CatalogHelp.RealAsync("llm"), BedrockConfig, Synthetic());

        UiThread.Run(() =>
        {
            Assert.True(withCatalogue.ViewModel.SelectGoal("main"));
            withCatalogue.GoTo("Provider and model");
            var model = withCatalogue.ViewModel.Steps.SelectMany(s => s.Fields).Single(f => f.Id == "model");
            model.Value = string.Empty;
            model.OpenModelList();
            withCatalogue.Host.Relayout();

            // One box is built for every field of the page (the other fields' are collapsed); the model field's is the one that shows.
            var picker = Assert.Single(VisualTree.Descendants<ModelPickerBox>(withCatalogue.Host.Content), p => p.IsVisible);
            Assert.Same(model, picker.DataContext);
            var list = VisualTree.Find<ListBox>(picker)!;
            Assert.True(list.IsVisible);
            Assert.Equal(new[] { "us.acme.large-v1:0", "us.acme.small-v1:0", "eu.acme.large-v1:0" }, list.Items.Cast<ModelPickerRow>().Select(r => r.Value).ToArray());
            Assert.Contains(withCatalogue.VisibleTexts(), t => t.Contains("3 model(s) from the runtime's catalogue for AWS Bedrock", StringComparison.Ordinal));

            // Typing narrows the list, and the typed text is the first row, marked as typed.
            model.Value = "small";
            withCatalogue.Host.Relayout();
            Assert.Equal(new[] { "small", "us.acme.small-v1:0" }, list.Items.Cast<ModelPickerRow>().Select(r => r.Value).ToArray());
            Assert.Contains(withCatalogue.VisibleTexts(), t => t == "not in the catalogue");
            RenderTo.Png(withCatalogue.Host, "llm-model-picker");

            // Choosing a row takes it into the box and closes the list.
            model.ChooseModelCommand.Execute(list.Items.Cast<ModelPickerRow>().Last());
            withCatalogue.Host.Relayout();
            Assert.Equal("us.acme.small-v1:0", model.Value);
            Assert.False(list.IsVisible);
        });

        using var without = new Scene(await CatalogHelp.RealAsync("llm"), BedrockConfig, catalogue: null);

        UiThread.Run(() =>
        {
            Assert.True(without.ViewModel.SelectGoal("main"));
            without.GoTo("Provider and model");

            Assert.DoesNotContain(VisualTree.Descendants<ModelPickerBox>(without.Host.Content), p => p.IsVisible);
            Assert.Contains(
                VisualTree.Descendants<Wpf.Ui.Controls.TextBox>(without.Host.Content).Where(b => b.IsVisible).Select(b => b.Text),
                text => text == "us.acme.large-v1:0");
        });
    }

    // ------------------------------------------------------------------ the guardrail's scope step

    [Fact]
    public async Task The_guardrail_scope_step_asks_one_connector_or_every_one_and_names_who_is_active()
    {
        using var scene = new Scene(await CatalogHelp.RealAsync("guardrail"), FleetConfig, catalogue: null);

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.SelectGoal("advanced"));
            scene.GoTo("Scope");
            var texts = scene.VisibleTexts();

            Assert.Contains("Scope", texts);
            Assert.Contains("Connector", texts);
            Assert.Contains("Turn the guardrail off", texts);

            // The roster, with each member's mode, is in the page's own words.
            Assert.Contains("Active connectors: claudecode (action), codex (observe).", scene.ViewModel.PageSubtitle, StringComparison.Ordinal);

            // A fleet starts on "one connector", so a global setting cannot be changed by accident, and the connector is not guessed.
            var combos = VisualTree.Descendants<ComboBox>(scene.Host.Content).Where(c => c.IsVisible).ToArray();
            Assert.Equal(2, combos.Length);
            var scope = scene.ViewModel.Steps.SelectMany(s => s.Fields).Single(f => f.Id == "scope");
            Assert.Equal(GuardrailScopes.Connector, scope.Value);
            Assert.Equal(string.Empty, scene.ViewModel.Steps.SelectMany(s => s.Fields).Single(f => f.Id == "connector").Value);
            Assert.DoesNotContain(texts, t => t.StartsWith("--scanner", StringComparison.Ordinal) || t == "(positional)");

            RenderTo.Png(scene.Host, "guardrail-scope-step");
        });
    }

    [Fact]
    public async Task Choosing_every_connector_shows_the_global_pages_and_choosing_one_takes_them_away()
    {
        using var scene = new Scene(await CatalogHelp.RealAsync("guardrail"), FleetConfig, catalogue: null);

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.SelectGoal("advanced"));
            var titles = () => scene.ViewModel.Steps.Where(s => s.IsVisible).Select(s => s.Title).ToArray();

            Assert.DoesNotContain("Scanner and detection", titles());
            Assert.DoesNotContain("Cisco AI Defense", titles());

            scene.ViewModel.Steps.SelectMany(s => s.Fields).Single(f => f.Id == "scope").Value = GuardrailScopes.Global;
            Assert.Contains("Scanner and detection", titles());
            Assert.Contains("Cisco AI Defense", titles());

            scene.ViewModel.Steps.SelectMany(s => s.Fields).Single(f => f.Id == "scope").Value = GuardrailScopes.Connector;
            Assert.DoesNotContain("Scanner and detection", titles());
        });
    }
}
