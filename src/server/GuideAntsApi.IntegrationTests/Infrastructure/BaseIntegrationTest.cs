using System.Net.Http.Headers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

[TestClass]
public abstract class BaseIntegrationTest : IAsyncDisposable
{
    private static readonly Guid DefaultAuthenticatedUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    protected static TestWebApplicationFactory? SharedFactory;
    protected HttpClient Client { get; private set; } = null!;

    // Method is called by derived test classes inside their own [ClassInitialize] wrappers
    public static async Task InitializeSharedFactoryAsync(TestContext context)
    {
        // Create one factory per test class (not per test method)
        SharedFactory = new TestWebApplicationFactory();
        await SharedFactory.InitializeAsync();
    }

    // Method is called by derived test classes inside their own [ClassCleanup] wrappers
    public static async Task DisposeSharedFactoryAsync()
    {
        if (SharedFactory != null)
        {
            await SharedFactory.DisposeAsync();
            SharedFactory = null;
        }
    }

    [TestInitialize]
    public virtual async Task BaseTestInitialize()
    {
        if (SharedFactory == null)
            throw new InvalidOperationException("SharedFactory is not initialized. Ensure ClassInitialize ran properly.");

        // Create a fresh client for each test method
        Client = SharedFactory.CreateClient();
        
        // Apply default test authentication token to each client
        SetupAuthentication();
        
        // Clean database state before each test
        await CleanDatabaseAsync();

        // Ensure gpt-4.1 model exists for tests
        using var scope = SharedFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GuideAntsApi.DataModel.ApplicationDbContext>();
        if (!await db.Models.AnyAsync(m => m.ModelId == "gpt-4.1"))
        {
            db.Models.Add(new GuideAntsApi.DataModel.Models.Model
            {
                ModelId = "gpt-4.1",
                Provider = "openai-chat",
                DisplayName = "GPT-4.1 Test",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        await EnsureRequiredAssistantsAsync(db);
    }

    [TestCleanup]
    public virtual Task BaseTestCleanup()
    {
        Client?.Dispose();
        return Task.CompletedTask;
    }

    public virtual async ValueTask DisposeAsync()
    {
        await BaseTestCleanup();
        GC.SuppressFinalize(this);
    }

    protected virtual async Task CleanDatabaseAsync()
    {
        // Override in derived classes if specific cleanup is needed
        await Task.CompletedTask;
    }

    private static async Task EnsureRequiredAssistantsAsync(ApplicationDbContext db)
    {
        // 1x1 PNG
        var pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO0K2B0AAAAASUVORK5CYII=");

        async Task EnsureAssistantAsync(string name, AssistantKind kind, bool withAvatar)
        {
            var existing = await db.Assistants.FirstOrDefaultAsync(a => a.Name == name);
            if (existing == null)
            {
                existing = new Assistant
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Kind = kind,
                    IsActive = true,
                    IsGlobal = true,
                    ModelId = "gpt-4.1",
                    Description = $"{name} test fixture",
                    Instructions = $"You are {name}."
                };
                db.Assistants.Add(existing);
            }
            else
            {
                existing.IsActive = true;
                existing.IsGlobal = true;
                existing.ModelId ??= "gpt-4.1";
                existing.Instructions ??= $"You are {name}.";
            }

            if (withAvatar)
            {
                existing.AvatarImageBytes = pngBytes;
                existing.AvatarContentType = "image/png";
            }
            else if (name.Equals("Read Web", StringComparison.OrdinalIgnoreCase))
            {
                existing.AvatarImageBytes = null;
                existing.AvatarContentType = null;
            }
        }

        await EnsureAssistantAsync("Code Executor", AssistantKind.Assistant, withAvatar: false);
        await EnsureAssistantAsync("Search", AssistantKind.Assistant, withAvatar: false);
        await EnsureAssistantAsync("Diagrams", AssistantKind.Assistant, withAvatar: true);
        await EnsureAssistantAsync("Read Web", AssistantKind.Assistant, withAvatar: false);
        await EnsureAssistantAsync("assistant", AssistantKind.Assistant, withAvatar: false);
        await EnsureAssistantAsync("python-assistant", AssistantKind.Assistant, withAvatar: false);
        await EnsureAssistantAsync("diagram-assistant", AssistantKind.Assistant, withAvatar: false);
        await EnsureAssistantAsync("Template Guide", AssistantKind.Guide, withAvatar: true);

        // Ensure at least one active guide exists for notebook creation endpoints.
        var anyGuide = await db.Assistants.AnyAsync(a => a.Kind == AssistantKind.Guide && a.IsActive);
        if (!anyGuide)
        {
            db.Assistants.Add(new Assistant
            {
                Id = Guid.NewGuid(),
                Name = "Test Guide",
                Kind = AssistantKind.Guide,
                IsActive = true,
                IsGlobal = true,
                ModelId = "gpt-4.1",
                Instructions = "Guide fixture"
            });
        }

        await db.SaveChangesAsync();
    }

    protected void SetupAuthentication(
        Role role = Role.Admin,
        string email = "integration.user@guideants.local",
        string name = "Integration Test User",
        Guid? userId = null,
        bool mustChangePassword = false)
    {
        var claims = BuildTokenClaims(
            userId ?? DefaultAuthenticatedUserId,
            role,
            email,
            name,
            mustChangePassword);
        var token = IntegrationTestAuthHandler.CreateToken(claims);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    protected void SetupAuthenticationWithClaims(params Claim[] claims)
    {
        var baselineClaims = BuildTokenClaims(
            DefaultAuthenticatedUserId,
            Role.Admin,
            "integration.user@guideants.local",
            "Integration Test User",
            mustChangePassword: false)
            .ToDictionary(claim => claim.Type, claim => claim, StringComparer.Ordinal);

        foreach (var claim in claims)
        {
            baselineClaims[claim.Type] = claim;
        }

        var token = IntegrationTestAuthHandler.CreateToken(baselineClaims.Values);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static List<Claim> BuildTokenClaims(
        Guid userId,
        Role role,
        string email,
        string name,
        bool mustChangePassword)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Role, role.ToString())
        };

        if (mustChangePassword)
        {
            claims.Add(new Claim("mustChangePassword", bool.TrueString));
        }

        return claims;
    }

    protected Task<Guid> GetDefaultGuideIdAsync() =>
        IntegrationTestGuideHelper.GetDefaultGuideIdAsync(SharedFactory!.Services);
} 
