using DefenseClaw.App.Services.Wizards;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Fluent glyph of a Setup tile. The mac app gives every wizard an SF Symbol (SetupDefinitions.swift); this is the same
/// choice in Fluent: connector cable -> plug, key -> key, AI Defense shield, brain for the LLM, network for the gateway,
/// checkered shield for the guardrail, a cube for the sandbox, books for the registries. Targets the CLI adds later fall back
/// to their group's glyph, so a new wizard still gets an icon without an app update.
/// </summary>
internal static class WizardTileIcons
{
    public static SymbolRegular For(string target, string group) => target switch
    {
        "llm" or "migrate-llm" => SymbolRegular.BrainCircuit20,
        "gateway" => SymbolRegular.Globe24,
        "guardrail" => SymbolRegular.ShieldCheckmark24,
        "guardrail-actions" => SymbolRegular.ShieldTask24,
        "trusted-paths" => SymbolRegular.ShieldKeyhole24,
        "ai-defense" => SymbolRegular.Shield24,
        "credentials" => SymbolRegular.Key24,
        "rotate-token" or "token-rotation" => SymbolRegular.ArrowSync24,
        "sandbox" => SymbolRegular.Cube24,
        "registry" or "registries" => SymbolRegular.Library24,
        "skill-scanner" => SymbolRegular.Wand24,
        "mcp-scanner" => SymbolRegular.Server24,
        "webhook" or "webhooks" => SymbolRegular.Link24,
        "local-observability" or "observability" => SymbolRegular.DataBarVertical24,
        "splunk" or "galileo" => SymbolRegular.DataTrending24,
        "notifications" or "notifications-set" or "notification-routing" => SymbolRegular.Alert24,
        "ai-discovery" => SymbolRegular.Bot24,
        "provider" or "custom-providers" => SymbolRegular.Flow16,
        _ => ForGroup(group),
    };

    private static SymbolRegular ForGroup(string group) => group switch
    {
        WizardGroups.Connectors => SymbolRegular.PlugConnected24,
        WizardGroups.Scanners => SymbolRegular.Search24,
        WizardGroups.GuardrailAndPolicy => SymbolRegular.ShieldCheckmark24,
        WizardGroups.Credentials => SymbolRegular.Key24,
        WizardGroups.Observability => SymbolRegular.DataBarVertical24,
        _ => SymbolRegular.Settings24,
    };
}
