using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// The runtime's own catalog behind the form (CUST-391): every load asks the installed runtime's Python for its Setup catalog
/// (<see cref="RuntimeCatalogLoader"/>, beside the <c>config show --source</c> read and at the same time), and the form is laid out in its sections
/// (<see cref="RuntimeCatalogLayout"/>) with an "Other (uncatalogued)" card for the rest. Where there is no runtime Python, or the read fails or times out,
/// the form is the source-driven one it always was, and the label beside Reload says "built-in catalog" (its tooltip says why): one quiet line, never a
/// banner. Same draft, same session, same watcher: nothing here holds a second copy of anything.
/// </summary>
public sealed partial class ConfigEditorWindowViewModel
{
    private RuntimeCatalogLoader? _catalogLoader;
    private RuntimePythonRunner? _catalogLoaderRunner;

    /// <summary>The last catalog read (its catalog, or why there is none); null before the first.</summary>
    private RuntimeCatalogResult? _catalogResult;

    /// <summary>The catalog the sections on screen are laid out in; null while they are the source-driven (built-in) ones.</summary>
    private RuntimeConfigCatalog? _layoutCatalog;

    /// <summary>Set by the Reload button: the next read asks the runtime again even when nothing it depends on changed.</summary>
    private bool _forceCatalogRead;

    /// <summary>
    /// Test seam: what starts the runtime's Python. Null in the app, where it is <see cref="RuntimeCatalogLoader.RunProcessAsync"/>, except in a view-model
    /// whose <c>config show</c> is supplied (<see cref="FormSourceOverride"/>): one that fakes the CLI never starts a real interpreter either, so it reads no
    /// catalog unless a test hands it this runner.
    /// </summary>
    internal RuntimePythonRunner? CatalogRunnerOverride { get; set; }

    /// <summary>True while the form is laid out in the runtime's catalog (the label says "runtime catalog").</summary>
    public bool UsesRuntimeCatalog => _layoutCatalog is not null;

    /// <summary>The loader this editor reads the catalog with (its cache and its run count), once it has asked.</summary>
    internal RuntimeCatalogLoader? CatalogLoader => _catalogLoader;

    /// <summary>Why the built-in catalog is in use, or null while the runtime's is (or before the first read).</summary>
    internal string? CatalogUnavailableReason => _catalogResult?.UnavailableReason;

    /// <summary>The production runner, one delegate for the app's lifetime (the loader, and so its cache, is kept for as long as the runner is the same).</summary>
    private static readonly RuntimePythonRunner ProcessRunner = RuntimeCatalogLoader.RunProcessAsync;

    private RuntimePythonRunner? CatalogRunner =>
        CatalogRunnerOverride ?? (FormSourceOverride is null ? ProcessRunner : null);

    /// <summary>The runtime's catalog for the file as it is now, or why there is none. Never throws.</summary>
    private async Task<RuntimeCatalogResult> ReadCatalogAsync()
    {
        var force = _forceCatalogRead;
        _forceCatalogRead = false;
        try
        {
            if (CatalogRunner is not { } runner)
            {
                return RuntimeCatalogResult.Unavailable("this editor's view of config.yaml is supplied, so the runtime is not asked");
            }

            if (_catalogLoader is null || !ReferenceEquals(_catalogLoaderRunner, runner))
            {
                _catalogLoader = new RuntimeCatalogLoader(runner);
                _catalogLoaderRunner = runner;
            }

            var request = new RuntimeCatalogRequest(
                _paths,
                _services.Installation.Context,
                _services.Runtime.Current.Identity?.Version,
                CliWorkingDirectory.DefaultPath);
            return await _catalogLoader.LoadAsync(request, force).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return RuntimeCatalogResult.Unavailable("the catalog could not be asked for: " + ex.Message);
        }
    }

    /// <summary>
    /// The form as it goes on screen: the source-driven sections laid out in the runtime's catalog when there is one and the source view parsed, else
    /// as they are. Records which it was, before the sections are published, so the label is right as they arrive.
    /// </summary>
    private IReadOnlyList<FormSection> LayOut(ConfigFormBuilder.BuildResult built, RuntimeConfigCatalog? catalog)
    {
        if (catalog is null || built.Warnings.Count > 0)
        {
            _layoutCatalog = null;
            return built.Sections;
        }

        _layoutCatalog = catalog;
        return RuntimeCatalogLayout.Arrange(catalog, built.Sections, OnFieldCommitted);
    }

    private void RaiseCatalogState()
    {
        OnPropertyChanged(nameof(UsesRuntimeCatalog));
        OnPropertyChanged(nameof(CatalogLabel));
        OnPropertyChanged(nameof(CatalogToolTip));
    }
}
