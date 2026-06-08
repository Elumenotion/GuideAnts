using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class ExcludedHostServiceTests
{
    private ApplicationDbContext _context = null!;
    private ExcludedHostService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options);
        _service = new ExcludedHostService(_context, NullLogger<ExcludedHostService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [TestMethod]
    public void NormalizeHost_ExtractsHostFromUrl()
    {
        var host = _service.NormalizeHost("https://Example.COM/path?q=1");

        host.Should().Be("example.com");
    }

    [TestMethod]
    public void IsHostExcluded_ReturnsTrue_ForExactAndSubdomainMatches()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };

        _service.IsHostExcluded("example.com", excluded).Should().BeTrue();
        _service.IsHostExcluded("docs.example.com", excluded).Should().BeTrue();
        _service.IsHostExcluded("example.org", excluded).Should().BeFalse();
    }

    [TestMethod]
    public async Task TryAddExcludedHostAsync_RefreshesExistingHost()
    {
        var first = await _service.TryAddExcludedHostAsync("https://example.com/path", "reason");
        var original = await _context.ExcludedHosts.SingleAsync();
        var originalUpdated = original.Updated;

        await Task.Delay(20);
        var second = await _service.TryAddExcludedHostAsync("EXAMPLE.COM", "reason");

        first.Should().BeTrue();
        second.Should().BeTrue();

        var all = await _service.GetExcludedHostsAsync();
        all.Should().BeEquivalentTo(["example.com"]);

        var refreshed = await _context.ExcludedHosts.SingleAsync();
        refreshed.Updated.Should().BeAfter(originalUpdated!.Value);
    }

    [TestMethod]
    public async Task GetExcludedHostsAsync_FiltersOutHostsOlderThanSevenDays()
    {
        var now = DateTime.UtcNow;
        _context.ExcludedHosts.AddRange(
            new ExcludedHost
            {
                Host = "fresh.example.com",
                Created = now.AddDays(-2),
                Updated = now.AddDays(-2)
            },
            new ExcludedHost
            {
                Host = "stale.example.com",
                Created = now.AddDays(-10),
                Updated = now.AddDays(-10)
            });
        await _context.SaveChangesAsync();

        var all = await _service.GetExcludedHostsAsync();

        all.Should().Contain("fresh.example.com");
        all.Should().NotContain("stale.example.com");
    }
}
