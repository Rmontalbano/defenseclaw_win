using System.Collections.Specialized;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The view-model rebuilds its row collections on the thread that applies a snapshot. With a dispatcher that is the UI thread, and the scanner
/// lookup (which must never wait there) comes back to it. With no synchronization context (every view-model test that is not on the UI thread) a
/// continuation would have resumed on a pool thread and rebuilt <c>ScannerRows</c> while the caller was still in <c>Apply</c>, which surfaced as
/// random <see cref="NullReferenceException"/>s in the Attention and Doctor tests about one run in six on a busy machine.
/// </summary>
public sealed class OverviewThreadingTests
{
    [Fact]
    public async Task With_no_dispatcher_the_scanner_rows_are_resolved_on_the_calling_thread_and_never_touched_from_another()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        await Task.Run(() =>
        {
            // A plain pool thread: no SynchronizationContext, like an xunit test body.
            Assert.Null(SynchronizationContext.Current);
            var caller = Environment.CurrentManagedThreadId;
            var threads = new List<int>();

            var vm = new OverviewPanelViewModel(services);
            ((INotifyCollectionChanged)vm.ScannerRows).CollectionChanged += (_, _) => threads.Add(Environment.CurrentManagedThreadId);

            // Resolved already: not the "checking" placeholder the UI thread shows until its pool lookup lands.
            Assert.DoesNotContain(vm.ScannerRows, row => row.StateText == "checking");

            for (var i = 0; i < 5; i++)
            {
                vm.Apply(services.Monitor.Current);
                Thread.Sleep(40);
            }

            // Whatever rebuilt the rows did it here.
            Assert.All(threads, thread => Assert.Equal(caller, thread));
        });
    }

    [Fact]
    public async Task Applying_many_times_from_a_plain_thread_never_races_the_rows()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        var faults = new List<Exception>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            try
            {
                var vm = new OverviewPanelViewModel(services);
                for (var i = 0; i < 50; i++)
                {
                    vm.Apply(services.Monitor.Current);
                    _ = vm.ScannerRows.ToList();
                    _ = vm.Attention.ToList();
                }
            }
            catch (Exception ex)
            {
                lock (faults)
                {
                    faults.Add(ex);
                }
            }
        })));

        Assert.Empty(faults);
    }
}
