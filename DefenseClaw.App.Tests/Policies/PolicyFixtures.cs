namespace DefenseClaw.App.Tests.Policies;

/// <summary>Synthetic <c>policy list</c> / <c>policy show</c> reports in the shape the 0.8.10 CLI prints.</summary>
internal static class PolicyFixtures
{
    public const string ListReport =
        "Available policies:\r\n\r\n" +
        "  * default [built-in] [active]\r\n      Balanced fixture policy\r\n" +
        "    bare-builtin [built-in]\r\n" +
        "    strict [built-in]\r\n      Strict fixture policy\r\n" +
        "    team-baseline\r\n      A custom fixture policy\r\n\r\n" +
        "  Activate a policy: defenseclaw policy activate <name>\r\n" +
        "  Show details:      defenseclaw policy show <name>\r\n";

    public static string Show(
        string name = "fixture",
        string install = "block",
        string runtime = "disable",
        int block = 4,
        int alert = 2,
        bool hilt = false,
        string trust = "full",
        string firewallDefault = "deny",
        int domains = 9,
        string ports = "[443, 80]",
        string extraOverride = "") =>
        $"Policy: {name}\n  A fixture\n\n" +
        "Admission:\n  scan_on_install:        True\n  allow_list_bypass_scan: True\n\n" +
        "Severity Actions:\n" +
        $"  CRITICAL    install={install,-5}  file=quarantine  runtime={runtime}\n" +
        "  HIGH        install=none   file=none        runtime=enable\n" +
        "  INFO        install=none   file=none        runtime=enable\n" +
        extraOverride +
        $"\nGuardrail:\n  block_threshold:    {block} (severity rank)\n  alert_threshold:    {alert} (severity rank)\n" +
        $"  hilt:               enabled={(hilt ? "True" : "False")} min=HIGH\n  cisco_trust_level:  {trust}\n" +
        "  patterns:\n    injection: 12 pattern(s)\n  severity_mappings:\n    injection: HIGH\n\n" +
        $"Firewall:\n  default_action:        {firewallDefault}\n  blocked_destinations:  2 entries\n  allowed_domains:       {domains} entries\n  allowed_ports:         {ports}\n\n" +
        "Enforcement:\n  max_enforcement_delay_seconds: 2\n\nAudit:\n  retention_days: 90\n";
}
