using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin;

internal static class Program
{
    /// <summary>
    /// The whole command line is one verb. Nothing else is read from it, and nothing else decides a path: see <see cref="AdminLayout"/>.
    /// </summary>
    internal static int Main(string[] args)
    {
        if (!AdminVerbs.TryParse(args, out var verb, out var problem))
        {
            Console.Error.WriteLine("DefenseClaw.GatewayAdmin: " + problem);
            return AdminExit.Usage;
        }

        var helper = new AdminHelper(
            new WindowsCallerIdentity(),
            new WindowsAdminFileSystem(),
            new MessageBoxConsent(),
            new WindowsProcessRunner(),
            new WindowsTokenOwner(),
            new PidFileGatewayProbe(),
            new WindowsOwnerFileSystem(),
            new SystemClock());

        var result = helper.Run(verb);
        foreach (var line in result.Lines)
        {
            Console.WriteLine(line);
        }

        return result.ExitCode;
    }
}
