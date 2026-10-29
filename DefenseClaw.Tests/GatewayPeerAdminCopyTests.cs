using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The second place a gateway may be sent the bearer token from: the administrator copy that "Run gateway as administrator" keeps in Program Files.
/// The verifier asks <see cref="IAdminCopyTrust"/> only for an image outside the installer's directories, with the process's name and the file's
/// name still required to be <c>defenseclaw-gateway</c>, the process in this app's Windows session, and the answer is read afresh each time.
/// Without a trust object nothing changes.
/// </summary>
public class GatewayPeerAdminCopyTests
{
    private const string Bin = @"C:\fake\install\bin";
    private const string CopyImage = @"C:\fake\pf\DefenseClaw Companion\AdminGateway\S-1-5-21-1-2-3-1001\defenseclaw-gateway.exe";

    private sealed class Trust : IAdminCopyTrust
    {
        public Func<string, bool> Answer { get; set; } = _ => true;

        public List<string> Asked { get; } = new();

        public bool IsTrustedImage(string imagePath)
        {
            Asked.Add(imagePath);
            return Answer(imagePath);
        }
    }

    private sealed class NoPortInspector : IPortOwnerInspector
    {
        public PortOwner? FindListener(int port) => null;
    }

    private static DefenseClawPaths Paths() =>
        new(dataDirectory: @"C:\fake\data", binDirectory: Bin, searchPath: Array.Empty<string>(), fileExists: _ => false);

    /// <summary>The app's Windows session in these tests; the copy is trusted only in it (CUST-362).</summary>
    private const int Session = 3;

    private static GatewayPeerVerifier Verifier(IAdminCopyTrust? trust) =>
        new(Paths(), new NoPortInspector(), adminCopy: trust, currentSessionId: Session, userProfile: PeerFixtures.FakeProfile);

    /// <summary>A process that opened its own socket (CUST-392).</summary>
    private static readonly DateTime Started = new(2026, 10, 29, 8, 0, 0, DateTimeKind.Utc);

    private static PortOwner Owner(string? name, string? image) =>
        new(4242, name, "127.0.0.1", 18970, image, Session, SocketCreatedUtc: Started.AddSeconds(1), ProcessCreatedUtc: Started);

    [Fact]
    public void A_trusted_administrator_copy_is_the_gateway()
    {
        var trust = new Trust();

        Assert.Equal(PortOwnerTrust.Gateway, Verifier(trust).Classify(Owner("defenseclaw-gateway", CopyImage)));
        Assert.Equal(new[] { CopyImage }, trust.Asked);
    }

    [Fact]
    public void An_administrator_copy_that_is_not_trusted_right_now_is_not_sent_the_token()
    {
        var trust = new Trust { Answer = _ => false };

        Assert.Equal(PortOwnerTrust.Other, Verifier(trust).Classify(Owner("defenseclaw-gateway", CopyImage)));
    }

    [Fact]
    public void Without_a_trust_object_the_copy_is_just_another_directory()
    {
        Assert.Equal(PortOwnerTrust.Other, Verifier(null).Classify(Owner("defenseclaw-gateway", CopyImage)));
    }

    [Fact]
    public void The_trust_answer_is_asked_again_every_time()
    {
        var trusted = true;
        var trust = new Trust { Answer = _ => trusted };
        var verifier = Verifier(trust);
        var owner = Owner("defenseclaw-gateway", CopyImage);

        Assert.Equal(PortOwnerTrust.Gateway, verifier.Classify(owner));
        trusted = false;
        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(owner));
        Assert.Equal(2, trust.Asked.Count);
    }

    [Theory]
    [InlineData("notepad", @"C:\fake\pf\DefenseClaw Companion\AdminGateway\S-1\defenseclaw-gateway.exe")]
    [InlineData("defenseclaw-gateway", @"C:\fake\pf\DefenseClaw Companion\AdminGateway\S-1\evil.exe")]
    [InlineData("defenseclaw-gateway", null)]
    [InlineData("defenseclaw-gateway", "")]
    public void The_name_and_the_file_name_are_still_required_before_the_trust_object_is_asked(string name, string? image)
    {
        var trust = new Trust();

        Assert.Equal(PortOwnerTrust.Other, Verifier(trust).Classify(Owner(name, image)));
        Assert.Empty(trust.Asked);
    }

    [Fact]
    public void The_installer_directory_is_trusted_without_asking_the_administrator_trust()
    {
        var trust = new Trust { Answer = _ => false };

        Assert.Equal(PortOwnerTrust.Gateway, Verifier(trust).Classify(Owner("defenseclaw-gateway", Bin + @"\defenseclaw-gateway.exe")));
        Assert.Empty(trust.Asked);
    }

    [Fact]
    public void One_untrusted_listener_among_several_makes_the_port_untrusted()
    {
        var verifier = Verifier(new Trust { Answer = path => path == CopyImage });
        var good = Owner("defenseclaw-gateway", CopyImage);
        var other = Owner("defenseclaw-gateway", @"C:\somewhere\defenseclaw-gateway.exe");

        Assert.Equal(PortOwnerTrust.Gateway, verifier.ClassifyAll(new[] { good }));
        Assert.Equal(PortOwnerTrust.Other, verifier.ClassifyAll(new[] { good, other }));
    }
}
