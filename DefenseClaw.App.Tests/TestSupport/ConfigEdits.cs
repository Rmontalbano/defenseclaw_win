using System.Reflection;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Edits the scratch installation's <c>config.yaml</c> and <c>.env</c> the way an editor or the CLI would, and raises the reload the watcher
/// raises after an edit (CUST-312). Every edit differs from every earlier one in length <i>and</i> in last-write time, which is set
/// explicitly: a test that waited on the file system's clock to tick would be one a loaded runner can fail. The contents are synthetic and
/// nothing outside the scratch directory is touched.
/// </summary>
internal sealed class ConfigEdits
{
    private static readonly DateTime T0 = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly AppServices _services;
    private int _config;
    private int _env;

    public ConfigEdits(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>A new config.yaml (valid YAML the app can load).</summary>
    public void EditConfig()
    {
        _config++;
        var path = _services.Paths.ConfigFilePath;
        File.WriteAllText(path, "guardrail:\n  mode: observe\n# edit " + new string('x', _config) + "\n");
        File.SetLastWriteTimeUtc(path, T0.AddMinutes(_config));
    }

    /// <summary>A new .env (a synthetic name and value; nothing reads it back).</summary>
    public void EditEnv()
    {
        _env++;
        var path = _services.Paths.EnvFilePath;
        File.WriteAllText(path, "EXAMPLE_NAME=" + new string('v', _env) + "\n");
        File.SetLastWriteTimeUtc(path, T0.AddMinutes(_env));
    }

    /// <summary>
    /// Raises <c>ConfigReloaded</c> (<see cref="AppServices.ReloadConfig"/> is what the watcher calls after an edit) and returns once every
    /// subscriber that was listening before this call has been told: the notification is marshalled to the UI thread when the process has one
    /// (other tests of the suite make one), so it can arrive after <c>ReloadConfig</c> returns. The wait is for that, not for a time.
    /// </summary>
    public void Reload()
    {
        // Not disposed: a notification that arrives after a timeout must still find an event to set (it would otherwise fault the shared UI thread).
        var heard = new ManualResetEventSlim();
        void Hear(object? sender, EventArgs e) => heard.Set();

        // Subscribed last, so it runs after every handler that was already attached: when it fires they have all run.
        _services.ConfigReloaded += Hear;
        try
        {
            _services.ReloadConfig();
            Assert.True(heard.Wait(TimeSpan.FromSeconds(60)), "ConfigReloaded was never raised.");
        }
        finally
        {
            _services.ConfigReloaded -= Hear;
        }
    }

    /// <summary>How many handlers listen to <c>AppServices.ConfigReloaded</c> (a field-like event, read the way the construction tests do).</summary>
    public static int Subscribers(AppServices services)
    {
        var field = typeof(AppServices).GetField(nameof(AppServices.ConfigReloaded), BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AppServices.ConfigReloaded is not a field-like event any more; update this helper.");
        return (field.GetValue(services) as Delegate)?.GetInvocationList().Length ?? 0;
    }
}
