namespace DefenseClaw.Core.Redaction;

/// <summary>
/// The 21 operations of the advanced redaction editor (the Mac's <c>RedactionAdvancedAction</c>). They are 20 commands of
/// <c>defenseclaw setup redaction</c>: <c>apply</c> is two operations, one per <c>--scope</c>. Fourteen change <c>config.yaml</c> (each has
/// <c>--dry-run</c>, <c>--json</c> and <c>--restart</c>); six only read.
/// </summary>
public enum RedactionOperation
{
    /// <summary><c>status --json</c>: the profile in force for every bucket and destination.</summary>
    Status,

    /// <summary><c>remove-all</c>: profile <c>none</c> on every configurable log and trace projection.</summary>
    RemoveAll,

    /// <summary><c>apply --scope all-configurable --profile P</c>.</summary>
    ApplyEverywhere,

    /// <summary><c>apply --scope defaults --profile P</c>.</summary>
    ApplyDefaults,

    /// <summary><c>defaults set [--profile P] [--logs|--no-logs] ...</c>.</summary>
    DefaultsSet,

    /// <summary><c>defaults reset</c>.</summary>
    DefaultsReset,

    /// <summary><c>bucket list</c> (text only: the command has no <c>--json</c>).</summary>
    BucketList,

    /// <summary><c>bucket set BUCKET [--profile P | --inherit-profile] [collection flags]</c>.</summary>
    BucketSet,

    /// <summary><c>bucket reset BUCKET</c>.</summary>
    BucketReset,

    /// <summary><c>profile list --json</c>.</summary>
    ProfileList,

    /// <summary><c>profile show NAME --json</c>.</summary>
    ProfileShow,

    /// <summary><c>profile set NAME --extends B [--detector D]... [--field CLASS=MODE]...</c>.</summary>
    ProfileSet,

    /// <summary><c>profile remove NAME [--replace-with P]</c>.</summary>
    ProfileRemove,

    /// <summary><c>destination show NAME</c> (text only).</summary>
    DestinationShow,

    /// <summary><c>destination send NAME --signal S... --bucket B... [--profile P]</c>.</summary>
    DestinationSend,

    /// <summary><c>destination inherit NAME</c>.</summary>
    DestinationInherit,

    /// <summary><c>route list DESTINATION --json</c>.</summary>
    RouteList,

    /// <summary><c>route add DESTINATION NAME [--position N]</c> and the route filters.</summary>
    RouteAdd,

    /// <summary><c>route set DESTINATION NAME</c> and the route filters (keeps the route's position).</summary>
    RouteSet,

    /// <summary><c>route move DESTINATION NAME --position N</c>.</summary>
    RouteMove,

    /// <summary><c>route remove DESTINATION NAME</c>.</summary>
    RouteRemove,
}

/// <summary>Where an operation sits in the editor's picker.</summary>
public enum RedactionGroup
{
    Policy,
    Defaults,
    Buckets,
    Profiles,
    Destinations,
    Routes,
}

/// <summary>The inputs an operation asks for; the editor shows exactly the controls its operation names.</summary>
[Flags]
public enum RedactionFields
{
    None = 0,

    /// <summary>One profile (<c>--profile</c>): required for <c>apply</c>, optional where the operation has other settings.</summary>
    Profile = 1 << 0,

    /// <summary><c>--inherit-profile</c>: a bucket gives up its own profile.</summary>
    InheritProfile = 1 << 1,

    /// <summary>One bucket, as the positional argument of <c>bucket set|reset</c>.</summary>
    Bucket = 1 << 2,

    /// <summary>One destination, as the positional argument.</summary>
    Destination = 1 << 3,

    /// <summary>A route's name (new for <c>route add</c>, an existing one for the others).</summary>
    RouteName = 1 << 4,

    /// <summary><c>--position</c>, one-based.</summary>
    Position = 1 << 5,

    /// <summary>One or more signals (<c>--signal</c>, repeatable).</summary>
    Signals = 1 << 6,

    /// <summary>One or more buckets, or all of them (<c>--bucket</c>, repeatable; <c>*</c> alone is every bucket).</summary>
    Buckets = 1 << 7,

    /// <summary>Whether logs, traces and metrics are collected (<c>--logs/--no-logs</c> and the others).</summary>
    Collect = 1 << 8,

    /// <summary>A profile's name, as the positional argument of <c>profile show|set|remove</c>.</summary>
    ProfileName = 1 << 9,

    /// <summary>The built-in profile a custom one starts from (<c>--extends</c>).</summary>
    Extends = 1 << 10,

    /// <summary>The detector groups a custom profile uses (<c>--detector</c>, repeatable).</summary>
    Detectors = 1 << 11,

    /// <summary>What a custom profile does to each field class (<c>--field CLASS=MODE</c>, repeatable).</summary>
    FieldModes = 1 << 12,

    /// <summary>The profile that takes over from a removed one (<c>--replace-with</c>).</summary>
    ReplaceWith = 1 << 13,

    /// <summary>The route selector (<c>--source</c>, <c>--connector</c>, <c>--producer-action</c>, <c>--event-name</c>, <c>--min-severity</c>) and <c>--route-action</c>.</summary>
    RouteFilters = 1 << 14,
}

/// <summary>What the editor knows about one operation before any command is built.</summary>
/// <param name="Operation">Which one.</param>
/// <param name="Title">The picker's label, the Mac's.</param>
/// <param name="Summary">One sentence on what it does.</param>
/// <param name="Group">Its group in the picker.</param>
/// <param name="Path">The words after <c>setup redaction</c> that name the command (<c>["route", "add"]</c>); <c>apply</c> has two operations on one path.</param>
/// <param name="IsMutation">True when it can change <c>config.yaml</c> (and so has a preview and a reviewed apply); false when it only reads.</param>
/// <param name="IsDestructive">True when it removes or resets something: the review is drawn as a destructive one.</param>
/// <param name="Fields">The inputs it asks for.</param>
/// <param name="Json">True when the command prints JSON (<c>--json</c>): every mutation, and the reads that have it.</param>
public sealed record RedactionOperationInfo(
    RedactionOperation Operation,
    string Title,
    string Summary,
    RedactionGroup Group,
    IReadOnlyList<string> Path,
    bool IsMutation,
    bool IsDestructive,
    RedactionFields Fields,
    bool Json)
{
    /// <summary>True when the operation names a destination whose policy the operator owns: not a generated one (<c>send</c>, <c>inherit</c>, every route operation).</summary>
    public bool NeedsConfigurableDestination => Fields.HasFlag(RedactionFields.Destination) && Operation != RedactionOperation.DestinationShow;

    /// <summary>The group's heading in the picker.</summary>
    public string GroupTitle => RedactionOperations.GroupTitle(Group);
}

/// <summary>The catalog of the 21 operations, in the Mac's order.</summary>
public static class RedactionOperations
{
    private static RedactionOperationInfo Read(
        RedactionOperation operation, string title, string summary, RedactionGroup group, string[] path, RedactionFields fields = RedactionFields.None, bool json = true) =>
        new(operation, title, summary, group, path, IsMutation: false, IsDestructive: false, fields, json);

    private static RedactionOperationInfo Change(
        RedactionOperation operation, string title, string summary, RedactionGroup group, string[] path, RedactionFields fields = RedactionFields.None, bool destructive = false) =>
        new(operation, title, summary, group, path, IsMutation: true, destructive, fields, Json: true);

    /// <summary>Every operation, in the order the picker lists them.</summary>
    public static IReadOnlyList<RedactionOperationInfo> All { get; } =
    [
        Read(RedactionOperation.Status, "Inspect effective policy", "Show the redaction profile in force for every bucket and destination.", RedactionGroup.Policy, ["status"]),
        Change(RedactionOperation.RemoveAll, "Remove all configurable redaction", "Send every configurable log and trace unredacted: profile none everywhere.", RedactionGroup.Policy, ["remove-all"], destructive: true),
        Change(RedactionOperation.ApplyEverywhere, "Apply profile everywhere", "Use one profile for every bucket and every destination you can configure.", RedactionGroup.Policy, ["apply"], RedactionFields.Profile),
        Change(RedactionOperation.ApplyDefaults, "Apply profile to defaults", "Change only the global default profile; a bucket or destination that names its own keeps it.", RedactionGroup.Policy, ["apply"], RedactionFields.Profile),
        Change(RedactionOperation.DefaultsSet, "Set global defaults", "Change the default profile and which signals are collected, for every bucket that does not set its own.", RedactionGroup.Defaults, ["defaults", "set"], RedactionFields.Profile | RedactionFields.Collect),
        Change(RedactionOperation.DefaultsReset, "Reset global defaults", "Put the default profile and collection back to the catalog's.", RedactionGroup.Defaults, ["defaults", "reset"], destructive: true),
        Read(RedactionOperation.BucketList, "List all buckets", "Print every bucket with the signals it collects and its profile.", RedactionGroup.Buckets, ["bucket", "list"], json: false),
        Change(RedactionOperation.BucketSet, "Set bucket policy", "Give one bucket its own profile or collection.", RedactionGroup.Buckets, ["bucket", "set"], RedactionFields.Bucket | RedactionFields.Profile | RedactionFields.InheritProfile | RedactionFields.Collect),
        Change(RedactionOperation.BucketReset, "Reset bucket policy", "Drop a bucket's own profile and collection so it follows the defaults.", RedactionGroup.Buckets, ["bucket", "reset"], RedactionFields.Bucket, destructive: true),
        Read(RedactionOperation.ProfileList, "List profiles", "Show the built-in and custom redaction profiles.", RedactionGroup.Profiles, ["profile", "list"]),
        Read(RedactionOperation.ProfileShow, "Show compiled profile", "Show a profile's detectors and what it does to each class of field.", RedactionGroup.Profiles, ["profile", "show"], RedactionFields.ProfileName),
        Change(RedactionOperation.ProfileSet, "Create or edit custom profile", "Build a profile from a built-in one: its detector groups and a mode for each field class.", RedactionGroup.Profiles, ["profile", "set"], RedactionFields.ProfileName | RedactionFields.Extends | RedactionFields.Detectors | RedactionFields.FieldModes),
        Change(RedactionOperation.ProfileRemove, "Remove custom profile", "Delete a custom profile, and point whatever used it at another profile.", RedactionGroup.Profiles, ["profile", "remove"], RedactionFields.ProfileName | RedactionFields.ReplaceWith, destructive: true),
        Read(RedactionOperation.DestinationShow, "Show destination policy", "Print one destination's effective redaction policy.", RedactionGroup.Destinations, ["destination", "show"], RedactionFields.Destination, json: false),
        Change(RedactionOperation.DestinationSend, "Set destination send policy", "Replace a destination's routing with one send policy: signals, buckets and a profile.", RedactionGroup.Destinations, ["destination", "send"], RedactionFields.Destination | RedactionFields.Signals | RedactionFields.Buckets | RedactionFields.Profile),
        Change(RedactionOperation.DestinationInherit, "Restore destination inheritance", "Remove a destination's own send policy and routes; it follows the defaults again.", RedactionGroup.Destinations, ["destination", "inherit"], RedactionFields.Destination),
        Read(RedactionOperation.RouteList, "List ordered routes", "Show a destination's routes in the order they are tried: the first match wins.", RedactionGroup.Routes, ["route", "list"], RedactionFields.Destination),
        Change(RedactionOperation.RouteAdd, "Add ordered route", "Add a route that sends or drops matching events, at a position or at the end.", RedactionGroup.Routes, ["route", "add"], RedactionFields.Destination | RedactionFields.RouteName | RedactionFields.Position | RedactionFields.Signals | RedactionFields.Buckets | RedactionFields.RouteFilters | RedactionFields.Profile),
        Change(RedactionOperation.RouteSet, "Replace ordered route", "Rewrite one route, keeping its place in the order.", RedactionGroup.Routes, ["route", "set"], RedactionFields.Destination | RedactionFields.RouteName | RedactionFields.Signals | RedactionFields.Buckets | RedactionFields.RouteFilters | RedactionFields.Profile),
        Change(RedactionOperation.RouteMove, "Move ordered route", "Move a route to another place in the first-match order.", RedactionGroup.Routes, ["route", "move"], RedactionFields.Destination | RedactionFields.RouteName | RedactionFields.Position),
        Change(RedactionOperation.RouteRemove, "Remove ordered route", "Delete one route from a destination.", RedactionGroup.Routes, ["route", "remove"], RedactionFields.Destination | RedactionFields.RouteName, destructive: true),
    ];

    /// <summary>The operations that change <c>config.yaml</c> (14 commands, 15 operations: <c>apply</c> counts twice).</summary>
    public static IEnumerable<RedactionOperationInfo> Mutations => All.Where(static o => o.IsMutation);

    /// <summary>The operations that only read.</summary>
    public static IEnumerable<RedactionOperationInfo> Reads => All.Where(static o => !o.IsMutation);

    public static RedactionOperationInfo Info(RedactionOperation operation) =>
        All.First(o => o.Operation == operation);

    /// <summary>The heading of a group in the picker.</summary>
    public static string GroupTitle(RedactionGroup group) => group switch
    {
        RedactionGroup.Policy => "Whole policy",
        RedactionGroup.Defaults => "Global defaults",
        RedactionGroup.Buckets => "Buckets",
        RedactionGroup.Profiles => "Profiles",
        RedactionGroup.Destinations => "Destinations",
        _ => "Routes",
    };
}
