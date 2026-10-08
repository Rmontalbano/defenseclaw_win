namespace DefenseClaw.Core.Policy;

/// <summary>
/// One <c>defenseclaw policy create NAME ...</c> command (0.8.10 <c>cmd_policy.create</c>). <c>create</c> writes a new YAML file into the
/// user's policy folder and changes nothing that is live: the new policy is inert until it is activated. A member left null or empty
/// is a flag not passed (so <c>--from-preset</c> keeps the preset's own value for it).
/// </summary>
public sealed record PolicyCreate
{
    public static readonly IReadOnlyList<string> Presets = new[] { "default", "strict", "permissive" };
    public static readonly IReadOnlyList<string> ActionLevels = new[] { "block", "warn", "allow" };

    /// <summary>The longest description the app will put on a command line.</summary>
    public const int MaxDescriptionLength = 200;

    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string FromPreset { get; init; } = string.Empty;
    public bool? ScanOnInstall { get; init; }
    public bool? AllowListBypass { get; init; }
    public string CriticalAction { get; init; } = string.Empty;
    public string HighAction { get; init; } = string.Empty;
    public string MediumAction { get; init; } = string.Empty;
    public string LowAction { get; init; } = string.Empty;

    /// <summary>What is wrong with this request, in a sentence; null when it is ready to review.</summary>
    /// <param name="existingNames">The names already listed: the CLI refuses to create over any of them.</param>
    public string? Validate(IEnumerable<string>? existingNames = null)
    {
        if (!PolicyNames.IsSafe(Name))
        {
            return $"A policy name is letters, digits, '.', '_' and '-' (up to {PolicyNames.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}), starting with a letter or digit.";
        }

        if (PolicyNames.Protected.Contains(Name))
        {
            return $"'{Name}' is a built-in policy; the CLI will not overwrite it.";
        }

        if (existingNames is not null && existingNames.Contains(Name, StringComparer.Ordinal))
        {
            return $"A policy named '{Name}' already exists. Delete it first or pick another name.";
        }

        if (Description.Length > MaxDescriptionLength)
        {
            return $"The description is longer than {MaxDescriptionLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters.";
        }

        if (Description.StartsWith('-') || Description != Description.Trim() || Description.Any(char.IsControl))
        {
            return "The description cannot start with '-', have spaces at either end or contain control characters.";
        }

        if (FromPreset.Length > 0 && !Presets.Contains(FromPreset, StringComparer.Ordinal))
        {
            return $"'{FromPreset}' is not a preset the CLI knows.";
        }

        foreach (var (label, level) in new[] { ("CRITICAL", CriticalAction), ("HIGH", HighAction), ("MEDIUM", MediumAction), ("LOW", LowAction) })
        {
            if (level.Length > 0 && !ActionLevels.Contains(level, StringComparer.Ordinal))
            {
                return $"'{level}' is not an action for {label} findings (block, warn or allow).";
            }
        }

        return null;
    }

    /// <summary>The arguments after <c>defenseclaw</c>. Only call when <see cref="Validate"/> returned null.</summary>
    public IReadOnlyList<string> ToArgv()
    {
        var argv = new List<string> { "policy", "create", Name };
        Add(argv, "--description", Description);
        Add(argv, "--from-preset", FromPreset);
        if (ScanOnInstall is { } scan)
        {
            argv.Add(scan ? "--scan-on-install" : "--no-scan-on-install");
        }

        if (AllowListBypass is { } bypass)
        {
            argv.Add(bypass ? "--allow-list-bypass" : "--no-allow-list-bypass");
        }

        Add(argv, "--critical-action", CriticalAction);
        Add(argv, "--high-action", HighAction);
        Add(argv, "--medium-action", MediumAction);
        Add(argv, "--low-action", LowAction);
        return argv;
    }

    private static void Add(List<string> argv, string flag, string value)
    {
        if (value.Length > 0)
        {
            argv.Add(flag);
            argv.Add(value);
        }
    }
}
