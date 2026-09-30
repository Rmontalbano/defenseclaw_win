using System.Runtime.CompilerServices;
using DefenseClaw.App.Views.Shell;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Installs the live-region hook the way the app does, before anything in this process has used
/// <c>AutomationProperties.LiveSetting</c>. The hook is an override of that property's metadata, which WPF only accepts for a
/// type that has not used the property yet, and every test shares one process: a panel built by an earlier test would otherwise
/// decide whether a later one can install it. In the app it is <c>AppearanceService.Initialize</c>, first thing at startup.
/// </summary>
internal static class LiveRegionBootstrap
{
#pragma warning disable CA2255 // A module initializer is the point: it has to run before any test touches WPF.
    [ModuleInitializer]
    internal static void Install() => LiveRegion.Install();
#pragma warning restore CA2255
}
