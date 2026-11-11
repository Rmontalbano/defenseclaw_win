using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services;

/// <summary>
/// The last Claude Cowork protection state the app read (runtime 1.0.8, CUST-445), shared by the Overview, the Setup readiness checklist and the Cowork
/// wizard. It is fed by the Overview's own <c>status --json</c> read (<see cref="Publish"/>, so no extra CLI call per refresh) and read again on demand
/// by the wizard's "Check again" and by a Setup visit that finds nothing recent (<see cref="RefreshAsync"/>, the same read-only command).
/// <para>
/// One per <see cref="AppServices"/> (<see cref="For"/>), so tests that build their own services never share one. Below 1.0.8
/// (<see cref="RuntimeCapability.CoworkProtection"/> absent) <see cref="Current"/> is always null and nothing is read.
/// </para>
/// </summary>
public sealed class CoworkProtectionStore
{
    private static readonly ConditionalWeakTable<AppServices, CoworkProtectionStore> Stores = new();

    private readonly AppServices _services;
    private CoworkProtection? _current;
    private int _reading;

    private CoworkProtectionStore(AppServices services) => _services = services;

    /// <summary>The store of <paramref name="services"/>.</summary>
    public static CoworkProtectionStore For(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Stores.GetValue(services, static s => new CoworkProtectionStore(s));
    }

    /// <summary>Raised (on the thread that published) when <see cref="Current"/> or <see cref="ReadAt"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>True when the running runtime has Cowork protection (1.0.8 or later).</summary>
    public bool Applies => _services.Runtime.Capabilities.Has(RuntimeCapability.CoworkProtection);

    /// <summary>The last state read; null before a read, when the runtime is older, or when <c>status --json</c> had no <c>cowork</c> object (nothing to say).</summary>
    public CoworkProtection? Current => Applies ? _current : null;

    /// <summary>When the last successful read landed; null before one.</summary>
    public DateTimeOffset? ReadAt { get; private set; }

    /// <summary>Why the last on-demand read failed; null after a good one.</summary>
    public string? Problem { get; private set; }

    /// <summary>A read that is still recent enough for a Setup visit to use it rather than run the command again.</summary>
    public bool IsFresh(TimeSpan maxAge) => ReadAt is { } at && DateTimeOffset.UtcNow - at < maxAge;

    /// <summary>Takes the <c>cowork</c> object of a <c>status --json</c> read someone else made (the Overview). Null means the object was absent.</summary>
    public void Publish(CoworkProtection? state, DateTimeOffset? readAt = null)
    {
        var changed = state != _current || ReadAt is null;
        _current = state;
        ReadAt = readAt ?? DateTimeOffset.UtcNow;
        Problem = null;
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Runs <c>defenseclaw status --json</c> (read-only, in Activity) and publishes its <c>cowork</c> object. One at a time; does nothing below 1.0.8.
    /// A failure leaves the last state and sets <see cref="Problem"/>. Returns the state after the read.
    /// </summary>
    public async Task<CoworkProtection?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!Applies)
        {
            return null;
        }

        if (Interlocked.Exchange(ref _reading, 1) == 1)
        {
            return Current;
        }

        try
        {
            var invocation = await DiscoverCli.RunReadOnlyAsync(_services, OverviewPanelViewModel.StatusArgv, cancellationToken).ConfigureAwait(true);
            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                Fail($"status --json did not finish: {reason}");
            }
            else if (invocation.ExitCode != 0)
            {
                Fail($"status --json exited {invocation.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "?"}");
            }
            else
            {
                Publish(CoworkProtection.FromStatusJson(DiscoverCli.TrimToJson(DiscoverCli.Stdout(invocation))));
            }
        }
        catch (CliNotFoundException ex)
        {
            Fail($"'{ex.ExecutableName}' was not found");
        }
        catch (JsonException)
        {
            Fail("status --json did not return JSON");
        }
        catch (SecretInArgumentException ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            _ = Interlocked.Exchange(ref _reading, 0);
        }

        return Current;
    }

    private void Fail(string problem)
    {
        Problem = problem;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
