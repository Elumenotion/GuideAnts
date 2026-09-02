using FluentAssertions;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
[DoNotParallelize]
public sealed class HostMountListingCacheSingleFlightTests
{
    [TestCleanup]
    public void Cleanup() => HostMountListingCache.ClearAll();

    [TestMethod]
    public void GetOrAdd_RunsFactoryOnceForConcurrentRequests()
    {
        var key = HostMountListingCache.ShallowKey(Guid.NewGuid().ToString("N"));
        var factoryCalls = 0;

        HostMountDirectoryScanner.ScanResult Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            Thread.Sleep(50);
            return new HostMountDirectoryScanner.ScanResult([], [], false);
        }

        Parallel.For(0, 8, _ => HostMountListingCache.GetOrAdd(key, Factory, TimeSpan.FromMinutes(1)));

        factoryCalls.Should().Be(1);
        HostMountListingCache.TryGet(key, out _).Should().BeTrue();
    }
}
