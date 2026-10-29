using DefenseClaw.Core.Net;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-362: which file a process runs, as an unelevated app can learn it. With a handle first (the buffer grown up to 32,768 characters, a
/// longer path refused rather than treated as hidden); without one, from the kernel's process list (<c>SystemProcessIdInformation</c>) and the
/// drive table, which names an elevated gateway's file too. Every case runs through the readers' seams; the only real calls ask about this test
/// process itself.
/// </summary>
public class ProcessImagePathTests
{
    private const string Copy = @"C:\Program Files\DefenseClaw Companion\AdminGateway\S-1-5-21-1-2-3-1001\defenseclaw-gateway.exe";
    private const string NtCopy = @"\Device\HarddiskVolume3\Program Files\DefenseClaw Companion\AdminGateway\S-1-5-21-1-2-3-1001\defenseclaw-gateway.exe";

    private static readonly IReadOnlyList<DosDevice> Drives = new[]
    {
        new DosDevice("C:", @"\Device\HarddiskVolume3"),
        new DosDevice("D:", @"\Device\HarddiskVolume30"),
        new DosDevice("S:", @"\??\C:\Users\operator\src"),
        new DosDevice("Z:", @"\Device\LanmanRedirector\;Z:000000000001a2b3\server\share"),
    };

    private sealed class Readers
    {
        public List<int> Capacities { get; } = new();

        public List<int> KernelAsked { get; } = new();

        public Func<int, (ProcessImagePath.HandleStatus, string?)> Handle { get; set; } = _ => (ProcessImagePath.HandleStatus.NotOpened, null);

        public string? Kernel { get; set; } = NtCopy;

        public ProcessImage? Read(int pid = 11524) =>
            ProcessImagePath.Read(
                pid,
                (_, capacity) =>
                {
                    Capacities.Add(capacity);
                    return Handle(capacity);
                },
                id =>
                {
                    KernelAsked.Add(id);
                    return Kernel;
                },
                () => Drives);
    }

    [Fact]
    public void A_process_that_opens_is_read_with_its_handle_and_the_kernel_is_not_asked()
    {
        var readers = new Readers { Handle = _ => (ProcessImagePath.HandleStatus.Read, Copy) };

        Assert.Equal(new ProcessImage(Copy, WithoutHandle: false), readers.Read());
        Assert.Equal(new[] { ProcessImagePath.FirstCapacity }, readers.Capacities);
        Assert.Empty(readers.KernelAsked);
    }

    [Fact]
    public void A_long_path_is_read_with_a_larger_buffer()
    {
        var longPath = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('d', 200), 10)) + @"\defenseclaw-gateway.exe";
        var readers = new Readers
        {
            Handle = capacity => capacity > longPath.Length ? (ProcessImagePath.HandleStatus.Read, longPath) : (ProcessImagePath.HandleStatus.BufferTooSmall, null),
        };

        Assert.Equal(new ProcessImage(longPath, WithoutHandle: false), readers.Read());
        Assert.Equal(new[] { 1024, 4096 }, readers.Capacities);
        Assert.Empty(readers.KernelAsked);
    }

    [Fact]
    public void A_path_too_long_for_any_buffer_is_refused_and_never_looked_up_another_way()
    {
        var readers = new Readers { Handle = _ => (ProcessImagePath.HandleStatus.BufferTooSmall, null) };

        Assert.Null(readers.Read());
        Assert.Equal(new[] { 1024, 4096, 16384, 32768 }, readers.Capacities);
        Assert.Empty(readers.KernelAsked);
    }

    [Theory]
    [InlineData(ProcessImagePath.HandleStatus.NotOpened)]
    [InlineData(ProcessImagePath.HandleStatus.Failed)]
    public void A_process_that_cannot_be_read_with_a_handle_is_read_from_the_kernels_list(ProcessImagePath.HandleStatus status)
    {
        var readers = new Readers { Handle = _ => (status, null) };

        Assert.Equal(new ProcessImage(Copy, WithoutHandle: true), readers.Read());
        Assert.Equal(new[] { 11524 }, readers.KernelAsked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\Device\HarddiskVolume9\Windows\evil\defenseclaw-gateway.exe")]
    [InlineData(@"\Device\Mup\server\share\defenseclaw-gateway.exe")]
    [InlineData(@"C:\Program Files\DefenseClaw Companion\AdminGateway\S-1-5-21-1-2-3-1001\defenseclaw-gateway.exe")]
    public void When_the_kernels_list_gives_nothing_usable_there_is_no_path(string? kernel)
    {
        var readers = new Readers { Kernel = kernel };

        Assert.Null(readers.Read());
    }

    [Fact]
    public void A_handle_that_answers_with_an_empty_path_is_no_path()
    {
        Assert.Null(new Readers { Handle = _ => (ProcessImagePath.HandleStatus.Read, "") }.Read());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void No_process_id_no_path(int pid)
    {
        var readers = new Readers { Handle = _ => (ProcessImagePath.HandleStatus.Read, Copy) };

        Assert.Null(readers.Read(pid));
        Assert.Empty(readers.Capacities);
    }

    // ---- the drive table ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Windows\notepad.exe", @"C:\Windows\notepad.exe")]
    [InlineData(@"\device\harddiskvolume3\Windows\notepad.exe", @"C:\Windows\notepad.exe")]
    [InlineData(@"\Device\HarddiskVolume30\tools\x.exe", @"D:\tools\x.exe")]
    public void A_kernel_path_becomes_the_drive_letter_path_of_its_one_device(string nt, string expected)
    {
        Assert.Equal(expected, ProcessImagePath.ToDosPath(nt, Drives));
    }

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3")]
    [InlineData(@"\Device\HarddiskVolume300\tools\x.exe")]
    [InlineData(@"\Device\HarddiskVolume\tools\x.exe")]
    [InlineData(@"\Device\Mup\server\share\x.exe")]
    [InlineData(@"\??\C:\Windows\notepad.exe")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("")]
    public void A_path_no_drive_names_exactly_is_refused(string nt)
    {
        Assert.Null(ProcessImagePath.ToDosPath(nt, Drives));
    }

    [Fact]
    public void Two_drives_naming_one_device_are_not_guessed_between()
    {
        var aliased = new[] { new DosDevice("C:", @"\Device\HarddiskVolume3"), new DosDevice("X:", @"\Device\HarddiskVolume3") };

        Assert.Null(ProcessImagePath.ToDosPath(@"\Device\HarddiskVolume3\Windows\notepad.exe", aliased));
    }

    [Fact]
    public void Entries_that_are_not_drive_letters_are_ignored()
    {
        var odd = new[] { new DosDevice("COM1", @"\Device\HarddiskVolume3"), new DosDevice("1:", @"\Device\HarddiskVolume3"), new DosDevice("C:", @"\Device\HarddiskVolume3") };

        Assert.Equal(@"C:\Windows\notepad.exe", ProcessImagePath.ToDosPath(@"\Device\HarddiskVolume3\Windows\notepad.exe", odd));
    }

    // ---- the real calls, about this process only -------------------------------------------------------------------------

    [Fact]
    public void This_process_reads_its_own_path_with_a_handle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var image = ProcessImagePath.Read(Environment.ProcessId);

        Assert.NotNull(image);
        Assert.Equal(Environment.ProcessPath, image!.Path, StringComparer.OrdinalIgnoreCase);
        Assert.False(image.WithoutHandle);
    }

    [Fact]
    public void The_kernels_list_names_this_process_without_a_handle_and_the_drive_table_turns_it_into_the_same_path()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var nt = ProcessImagePath.ReadKernelImageName(Environment.ProcessId);

        Assert.NotNull(nt);
        Assert.StartsWith(@"\Device\", nt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Environment.ProcessPath, ProcessImagePath.ToDosPath(nt, ProcessImagePath.ReadDosDevices()), StringComparer.OrdinalIgnoreCase);
        Assert.Null(ProcessImagePath.ReadKernelImageName(0));
    }
}
