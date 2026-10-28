namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// <c>setup observability add</c>: the two switches that let a destination be a collector the operator runs themselves (CUST-379).
/// <para>
/// <c>--allow-private-networks</c> ("Allow an endpoint on this computer or a private network", cmd_setup_observability.py:156, in 1.0.0 as well as 1.0.4) is refused
/// by the CLI for a loopback or private address unless it is given; the CLI's own error tells the operator to add it. The wizard offers it as what it is: an explicit
/// opt-in, off by default, with the reason spelled out beside it, and with its <c>--plaintext</c> companion (OTLP without TLS, which only makes sense for such a
/// collector). Both come from the live help, so a runtime that lacks either simply does not show it. The review adds a bar when the opt-in is on (<see cref="WizardCautions"/>).
/// </para>
/// </summary>
public static partial class WizardStepFactory
{
    /// <summary>The flag that lets a destination be on this computer or a private network.</summary>
    internal const string AllowPrivateNetworksFlag = "--allow-private-networks";

    /// <summary>The OTLP companion: no TLS.</summary>
    internal const string PlaintextFlag = "--plaintext";

    /// <summary>The field with the words the add wizard uses for the two flags; any other field (or any other command) comes back as it is.</summary>
    private static WizardField ObservabilityAddWording(string? target, string subcommand, WizardField field)
    {
        if (!string.Equals(target, "observability", StringComparison.Ordinal) || !string.Equals(subcommand, "add", StringComparison.Ordinal))
        {
            return field;
        }

        return field.Flag switch
        {
            AllowPrivateNetworksFlag => field.WithWording(
                "Allow a private or loopback endpoint",
                "Off by default, and the CLI refuses an endpoint on this computer or a private network without it. Turn it on only for a collector you run yourself " +
                "(for example one on 127.0.0.1 or 10.x). Telemetry then goes to that address, so check it is yours. For the bundled local stack, use the local-otlp " +
                "preset instead."),
            PlaintextFlag => field.WithWording(
                "Send without TLS",
                "Off by default. Sends OTLP without TLS, for a collector you run yourself that has none; use it with a private or loopback endpoint. " +
                "Anyone on the network path can read what is sent."),
            _ => field,
        };
    }
}
