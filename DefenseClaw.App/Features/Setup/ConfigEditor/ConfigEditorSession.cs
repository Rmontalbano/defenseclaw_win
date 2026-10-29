using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>
/// The app's one config.yaml draft (CUST-385): a single <see cref="ConfigEditorWindowViewModel"/> per composition, shown by the Setup panel's inline
/// editor and by the detached <see cref="ConfigEditorWindow"/> alike. An edit in one is the edit in the other, leaving the Setup panel and coming back
/// keeps it, and there is never a second draft or a second reaction to the file changing on disk.
/// <para>
/// <b>What the views share and what they do not.</b> The buffer, the form tree, the banners, the review and the save pipeline are the view-model's, so
/// there is one of each. What only a view can do is attached here: commit a value still inside one of its FORM boxes (<see cref="ConfigEditorView.CommitPendingFormEdit"/>,
/// asked of every view before anything counts the edits), and be the window a question is asked over (the view the operator used last, else the detached
/// window, else the dashboard).
/// </para>
/// <para>
/// <b>Watching the file.</b> The view-model reacts to the shell's "config.yaml changed" notice (<see cref="AppServices.ConfigReloaded"/>) only while a view is on
/// screen: one subscription, taken when the first view comes up and dropped when the last one goes. A view that comes back catches up once
/// (<see cref="ConfigEditorWindowViewModel.HandleDiskChangeAsync"/> is a no-op when the file did not move), so a hidden editor never re-reads the runtime for nobody.
/// </para>
/// <para>
/// <b>Closing and exiting.</b> Closing the detached window drops nothing - the draft stays in the Setup panel's editor - so the window no longer asks. The tray
/// Exit still does whenever the draft has unsaved edits, window or not (<see cref="ConfigEditorWindow.CloseForExitAsync(AppServices)"/>).
/// </para>
/// </summary>
public sealed class ConfigEditorSession
{
    private static readonly ConditionalWeakTable<AppServices, ConfigEditorSession> Sessions = new();

    private readonly List<ConfigEditorView> _views = new();
    private ConfigEditorView? _lastUsed;
    private bool _exitFlowRunning;

    private ConfigEditorSession(AppServices services)
    {
        Services = services;
        Editor = new ConfigEditorWindowViewModel(services)
        {
            // An unanswered question (no UI attached) is Cancel inside the view-model; here the question is a real dialog over the view in use.
            UnsavedChangesPrompt = request => Task.FromResult(UnsavedChangesDialog.Ask(Owner(), request)),
            CommitPendingEdits = CommitPendingEdits,
        };
        WireGatewayRestart();
    }

    /// <summary>The composition this draft edits the config.yaml of.</summary>
    public AppServices Services { get; }

    /// <summary>The draft itself: the one view-model every config editor view binds to.</summary>
    public ConfigEditorWindowViewModel Editor { get; }

    /// <summary>How many views are attached right now (on screen). Tests read it; nothing else needs it.</summary>
    internal int AttachedViewCount => _views.Count;

    /// <summary>The session of <paramref name="services"/>, made on first use. Every caller gets the same one for the same composition.</summary>
    public static ConfigEditorSession For(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Sessions.GetValue(services, static s => new ConfigEditorSession(s));
    }

    /// <summary>The session of <paramref name="services"/> when one was ever made; null otherwise (nothing to ask on exit).</summary>
    internal static ConfigEditorSession? TryGet(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Sessions.TryGetValue(services, out var session) ? session : null;
    }

    /// <summary>
    /// Reads config.yaml and the runtime's view of it, the first time anything asks; later calls do nothing, so a second view never replaces the draft.
    /// A read only (<c>defenseclaw config show --source</c>, read-only by <c>CommandTiers</c>): nothing is written.
    /// </summary>
    public Task EnsureLoadedAsync() => Editor.HasLoadStarted ? Task.CompletedTask : Editor.LoadAsync();

    /// <summary>A view came on screen: it can now be asked to commit a pending FORM edit, and the draft follows the file while any view is up.</summary>
    internal void Attach(ConfigEditorView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (_views.Contains(view))
        {
            return;
        }

        _views.Add(view);
        _lastUsed ??= view;
        WireGatewayRestart();

        if (_views.Count == 1)
        {
            Editor.WatchDiskChanges();

            // The file may have moved while nothing was showing the draft: one look, which does nothing when it did not.
            if (Editor.HasLoadStarted && !Editor.IsLoading)
            {
                _ = Editor.HandleDiskChangeAsync();
            }
        }
    }

    /// <summary>A view left the screen. The draft stays; with no view left, it stops following the file until one comes back.</summary>
    internal void Detach(ConfigEditorView view)
    {
        if (!_views.Remove(view))
        {
            return;
        }

        if (ReferenceEquals(_lastUsed, view))
        {
            _lastUsed = _views.Count > 0 ? _views[^1] : null;
        }

        if (_views.Count == 0)
        {
            Editor.StopWatchingDiskChanges();
        }
    }

    /// <summary>The operator is working in <paramref name="view"/>: questions are asked over its window from now on.</summary>
    internal void NoteUsed(ConfigEditorView view)
    {
        if (_views.Contains(view))
        {
            _lastUsed = view;
        }
    }

    /// <summary>
    /// The tray Exit's question when no detached window is open: the draft lives in the Setup panel's editor. <see cref="ConfigEditorExitResult.Closed"/>
    /// when there is nothing to lose, the operator discarded, or the save went through and validated; <see cref="ConfigEditorExitResult.Declined"/> when the
    /// operator cancelled or the save failed; <see cref="ConfigEditorExitResult.Busy"/> when a question is already open.
    /// </summary>
    internal async Task<ConfigEditorExitResult> ConfirmExitAsync()
    {
        if (_exitFlowRunning || Editor.IsAskingAboutUnsavedChanges)
        {
            return ConfigEditorExitResult.Busy;
        }

        _exitFlowRunning = true;
        try
        {
            if (!Editor.CloseNeedsConfirmation())
            {
                return ConfigEditorExitResult.Closed;
            }

            // The question is about the Setup panel's draft: put it on screen to be read (the shell shows the window and the panel; a test host has no shell).
            Services.Navigation.Request(new NavigationRequest("setup", ConfigEditorFocus.Instance));

            return await Editor.ConfirmCloseAsync().ConfigureAwait(true)
                ? ConfigEditorExitResult.Closed
                : ConfigEditorExitResult.Declined;
        }
        finally
        {
            _exitFlowRunning = false;
        }
    }

    /// <summary>The window a question or the reviewed gateway restart is shown over: the view used last, else the detached window, else the dashboard.</summary>
    internal Window? Owner()
    {
        if (_lastUsed is { } view && Window.GetWindow(view) is { IsVisible: true } window)
        {
            return window;
        }

        return ConfigEditorWindow.OpenWindow ?? Application.Current?.MainWindow;
    }

    private void CommitPendingEdits()
    {
        foreach (var view in _views.ToArray())
        {
            view.CommitPendingFormEdit();
        }
    }

    /// <summary>
    /// The post-save bar's "Restart gateway…": the tray's reviewed restart (<see cref="ConfigEditorWindow.GatewayRestart"/>, set by the dashboard), over the
    /// window in use. Left unset in a host without a dashboard, which leaves the bar without a button rather than with one that does nothing; never replaces
    /// a request someone else already set.
    /// </summary>
    private void WireGatewayRestart()
    {
        if (Editor.RestartGatewayRequest is null && ConfigEditorWindow.GatewayRestart is { } restart)
        {
            Editor.RestartGatewayRequest = () => Owner() is { } owner ? restart(owner) : Task.CompletedTask;
        }
    }
}
