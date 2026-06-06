using System.Text.Json;
using System.Security.Claims;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.UserProjectContextOptions;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class ContextOptionsServiceTests
{
    [TestMethod]
    public async Task BuildContextMessageAsync_ResolvesSingleUserSmartValues()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Name = "Olivia Lite",
            Email = "olivia@example.com",
            Created = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc),
        });
        db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            Role = Role.Admin
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, userId);
        var assistant = new AssistantDefinition
        {
            ContextOptions = new Dictionary<string, string>
            {
                ["user.name"] = "[@userName]",
                ["user.email"] = "[@userEmail]",
            },
        };

        var message = await service.BuildContextMessageAsync(
            assistant,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        var contextOptions = ParseContextOptions(message);
        contextOptions["user.name"].Should().Be("Olivia Lite");
        contextOptions["user.email"].Should().Be("olivia@example.com");
    }

    [TestMethod]
    public async Task ResolveAsync_UsesProjectScopedUserContextOptionOverrides()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Name = "Olivia Lite",
            Email = "olivia@example.com",
            Created = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc),
        });
        db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            Role = Role.Admin
        });
        db.UserProjectContextOptions.Add(new UserProjectContextOption
        {
            UserId = userId,
            ProjectId = projectId,
            Key = "user.email",
            Value = "project-specific@example.com",
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, userId);
        var assistant = new AssistantDefinition
        {
            ContextOptions = new Dictionary<string, string>
            {
                ["user.email"] = "[@userEmail]",
            },
        };

        var resolved = await service.ResolveAsync(
            assistant,
            projectId,
            Guid.NewGuid(),
            Guid.NewGuid());

        resolved["user.email"].Should().Be("project-specific@example.com");
    }

    private static ContextOptionsService CreateService(ApplicationDbContext db, Guid userId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            authenticationType: "TestAuth"));
        var userContext = new TestUserContext(principal);
        var currentUserService = new CurrentUserService(db, userContext);

        return new ContextOptionsService(
            db,
            currentUserService,
            new UserProjectContextOptionsService(db));
    }

    private sealed class TestUserContext : IUserContext
    {
        public TestUserContext(ClaimsPrincipal principal)
        {
            Principal = principal;
        }

        public ClaimsPrincipal? Principal { get; }
    }

    private static Dictionary<string, string> ParseContextOptions(string? message)
    {
        message.Should().NotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(message!);
        var root = doc.RootElement.GetProperty("contextOptions");
        return root.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"context-options-{Guid.NewGuid():N}")
            .Options;

        return new ApplicationDbContext(options);
    }
}
