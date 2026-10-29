using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DefenseClaw.Core.Config;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// The notes the FORM tab shows about config.yaml as the 1.0.4 runtime reads it (CUST-377), derived from the file's own text and shown only for a runtime that
/// has them (<see cref="DefenseClaw.Core.Runtime.RuntimeCapability.Config104Keys"/>); an older runtime gets none and looks as it did.
/// <list type="bullet">
/// <item><description><b>Splunk <c>local_stack</c></b> (schemas/config/v8: <c>observability.destinations[].local_stack</c>, "written only by local Splunk setup"): the record that
/// DefenseClaw started the local Splunk container the destination sends to. It is shown here, read-only, so nobody edits or copies it by hand; every Splunk
/// destination without it is remote.</description></item>
/// <item><description><b>Profile assignments</b> (CHANGELOG 1.0.4, "Guardrail profiles"): when several profiles are proven to match, their strictest combination applies. Before
/// 1.0.4 the first match won, so the order of <c>guardrail.profile_assignments</c> no longer decides.</description></item>
/// </list>
/// A note carries a destination's name and a container id's first characters at most; never a token, an endpoint or any other value.
/// </summary>
public static class ConfigRuntimeNotes
{
    /// <summary>How many characters of the container id a note shows.</summary>
    private const int ContainerIdChars = 12;

    /// <summary>The longest destination name a note quotes.</summary>
    private const int MaxNameChars = 48;

    /// <summary>The notes for <paramref name="configText"/>; empty when <paramref name="runtimeHas104Keys"/> is false or the text does not parse.</summary>
    public static IReadOnlyList<string> For(string configText, bool runtimeHas104Keys)
    {
        ArgumentNullException.ThrowIfNull(configText);

        if (!runtimeHas104Keys || string.IsNullOrWhiteSpace(configText) || ConfigYamlGuard.Refusal(configText) is not null)
        {
            return [];
        }

        YamlMappingNode? root;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(configText);
            stream.Load(reader);
            root = stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
        }
        catch (YamlException)
        {
            return [];
        }

        if (root is null)
        {
            return [];
        }

        var notes = new List<string>();
        AddLocalStackNotes(root, notes);

        if (Get(root, "guardrail") is { } guardrail &&
            Get(guardrail, "profile_assignments") is YamlSequenceNode { Children.Count: > 0 })
        {
            notes.Add(
                "Guardrail profile assignments: when several profiles match, the runtime applies their strictest combination (1.0.4). The first match no longer wins, so the order of the assignments does not decide.");
        }

        return notes;
    }

    private static void AddLocalStackNotes(YamlMappingNode root, List<string> notes)
    {
        if (Get(root, "observability") is not { } observability || Get(observability, "destinations") is not YamlSequenceNode destinations)
        {
            return;
        }

        foreach (var node in destinations.Children.OfType<YamlMappingNode>())
        {
            if (Get(node, "local_stack") is not YamlMappingNode stack)
            {
                continue;
            }

            var name = Quote(Scalar(Get(node, "name")));
            var id = Scalar(Get(stack, "container_id"));
            var shown = id.Length > ContainerIdChars ? id[..ContainerIdChars] + "…" : id;
            var container = shown.Length == 0 ? string.Empty : $" (container {shown})";
            notes.Add(
                $"Splunk destination {name} was set up by DefenseClaw's local Splunk{container}. Its local_stack record is written only by that setup and is shown read-only: change it with the local Splunk setup, not by hand. A Splunk destination without it is remote.");
        }
    }

    private static string Quote(string name)
    {
        var clean = new string(name.Where(static c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0)
        {
            return "(unnamed)";
        }

        return "\"" + (clean.Length > MaxNameChars ? clean[..MaxNameChars] + "…" : clean) + "\"";
    }

    private static YamlNode? Get(YamlNode? node, string key) =>
        node is YamlMappingNode map
            ? map.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode { Value: { } text } && string.Equals(text, key, StringComparison.Ordinal)).Value
            : null;

    private static string Scalar(YamlNode? node) => node is YamlScalarNode { Value: { } text } ? text : string.Empty;
}
