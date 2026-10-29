# Agents (Windows extension)

The Agents panel has no counterpart in the Mac app: the Mac's `Features/` folder has no agents view and its sidebar has no `agents` entry. This page is a Windows extension (CUST-355): an in-app view over the gateway's correlation ledger (`DefenseClaw.Core/Correlation`), listed in the Discover group after the Mac's four Discover pages with a Windows-only tag (`Services/PanelCatalog.cs`), and gated on `RuntimeCapability.CorrelationApi`.

Like every folder under `Features/`, this one groups files by surface only. Namespaces were left as they were (`DefenseClaw.App.Views.Panels`, `DefenseClaw.App.ViewModels`); see `docs/MAC-WINDOWS-SURFACE-MAP.md` Section 8.
