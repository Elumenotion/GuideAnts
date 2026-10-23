using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.DataModel;

[TestClass]
public sealed class SaveChangesUtcGuardTests
{
    private static readonly DateTime UtcInstant = new(2026, 10, 23, 10, 30, 15, 123, DateTimeKind.Utc);

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"utc-guard-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }

    private static User CreateUser() => new()
    {
        Name = "Guard Test",
        Email = "guard-test@example.com"
    };

    [TestMethod]
    public async Task SaveChanges_UtcValue_PassesThroughUnchanged()
    {
        await using var db = CreateContext();
        var user = CreateUser();
        db.Users.Add(user);
        user.Created = UtcInstant;

        await db.SaveChangesAsync();

        var loaded = await db.Users.AsNoTracking().SingleAsync();
        loaded.Created.Kind.Should().Be(DateTimeKind.Utc);
        loaded.Created.Should().Be(UtcInstant);
    }

    [TestMethod]
    public async Task SaveChanges_UnspecifiedValue_IsMarkedUtc()
    {
        // EF reads and JSON parsing produce Kind=Unspecified; by store convention that is a UTC instant.
        await using var db = CreateContext();
        var user = CreateUser();
        db.Users.Add(user);
        user.Created = DateTime.SpecifyKind(UtcInstant, DateTimeKind.Unspecified);
        user.LastLoginAt = DateTime.SpecifyKind(UtcInstant, DateTimeKind.Unspecified);

        await db.SaveChangesAsync();

        // The Unspecified values were accepted (not rejected) and stored as the UTC instant.
        // (datetime2 stores no offset, so a read back is Kind=Unspecified by design; the
        // instant is the storage-stable guarantee. The guard's write-time normalization to
        // Kind=Utc is proven by the Local-throw tests, which fire before the write.)
        var loaded = await db.Users.AsNoTracking().SingleAsync();
        loaded.Created.Should().Be(UtcInstant);
        loaded.LastLoginAt!.Value.Should().Be(UtcInstant);
    }

    [TestMethod]
    public async Task SaveChanges_LocalValue_Throws()
    {
        // A local wall-clock value stored as-is would read back hours off; the guard makes that loud.
        await using var db = CreateContext();
        var user = CreateUser();
        db.Users.Add(user);
        user.Created = DateTime.SpecifyKind(UtcInstant, DateTimeKind.Local);

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Local DateTime*Created*UTC*");
    }

    [TestMethod]
    public async Task SaveChanges_LocalNullableValue_Throws()
    {
        await using var db = CreateContext();
        var user = CreateUser();
        db.Users.Add(user);
        user.LastLoginAt = DateTime.SpecifyKind(UtcInstant, DateTimeKind.Local);

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Local DateTime*LastLoginAt*UTC*");
    }

    [TestMethod]
    public async Task SaveChanges_NullNullableValue_Passes()
    {
        await using var db = CreateContext();
        var user = CreateUser();
        db.Users.Add(user);
        user.Created = UtcInstant;
        user.LastLoginAt = null;

        await db.SaveChangesAsync();

        var loaded = await db.Users.AsNoTracking().SingleAsync();
        loaded.LastLoginAt.Should().BeNull();
    }

    [TestMethod]
    public async Task SaveChanges_ModifiedEntity_ChangedUnspecifiedProperty_IsMarkedUtc()
    {
        await using var db = CreateContext();
        var user = CreateUser();
        db.Users.Add(user);
        user.Created = UtcInstant;
        await db.SaveChangesAsync();

        user.LastLoginAt = DateTime.SpecifyKind(UtcInstant, DateTimeKind.Unspecified);
        await db.SaveChangesAsync();

        // The changed Unspecified property was accepted and stored as the UTC instant.
        var loaded = await db.Users.AsNoTracking().SingleAsync();
        loaded.LastLoginAt!.Value.Should().Be(UtcInstant);
    }

    [TestMethod]
    public async Task SaveChanges_ModifiedEntity_ChangedLocalProperty_Throws()
    {
        await using var db = CreateContext();
        var user = CreateUser();
        db.Users.Add(user);
        user.Created = UtcInstant;
        await db.SaveChangesAsync();

        user.LastLoginAt = DateTime.SpecifyKind(UtcInstant, DateTimeKind.Local);

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Local DateTime*LastLoginAt*UTC*");
    }

    [TestMethod]
    public void SaveChanges_Sync_LocalValue_Throws()
    {
        using var db = CreateContext();
        var user = CreateUser();
        db.Users.Add(user);
        user.Created = DateTime.SpecifyKind(UtcInstant, DateTimeKind.Local);

        var act = () => db.SaveChanges();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Local DateTime*Created*UTC*");
    }
}
