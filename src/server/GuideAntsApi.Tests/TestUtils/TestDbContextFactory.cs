using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.TestUtils;

internal sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
{
    /// <summary>
    /// Factory whose contexts target the named InMemory database. Used by the
    /// ApplicationSettingsService concurrency tests: EF 8's in-memory provider
    /// exposes no database identity on the context (and
    /// <c>Database.GetConnectionString()</c> is relational-only and throws here),
    /// so the fixture keeps the per-test database name in a static field next to
    /// its CreateDbContext helper.
    /// </summary>
    public static TestDbContextFactory FromInMemory(string databaseName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(databaseName).Options);

    public ApplicationDbContext CreateDbContext() => new(options);

    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
