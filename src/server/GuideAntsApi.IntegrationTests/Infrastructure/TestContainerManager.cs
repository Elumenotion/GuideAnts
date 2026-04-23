using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using GuideAntsApi.DataModel;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

public class TestContainerManager : IAsyncDisposable
{
    private static readonly Lazy<TestContainerManager> _instance = new(() => new TestContainerManager());
    private MsSqlContainer? _container;
    private string? _connectionString;
    private bool _initialized;
    private readonly object _lock = new object();

    public static TestContainerManager Instance => _instance.Value;

    private TestContainerManager()
    {
        // Don't initialize in constructor - do it in EnsureInitializedAsync
    }

    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        lock (_lock)
        {
            if (_initialized) return;

            _container = new MsSqlBuilder()
                .WithImage("mssql2025-dev-fts")
                .WithPassword("Your_password123")
                .Build();
        }

        await _container.StartAsync();
        var baseConnectionString = _container.GetConnectionString();
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = "GuideAntsApiTest"
        };
        _connectionString = builder.ConnectionString;

        // Initialize database
        await InitializeDatabaseAsync();
        _initialized = true;
    }

    private async Task InitializeDatabaseAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(_connectionString));

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.Database.MigrateAsync();
    }

    public Task<string> GetConnectionStringAsync()
    {
        if (!_initialized || _connectionString == null)
            throw new InvalidOperationException("Container not initialized. Call EnsureInitializedAsync first.");
        
        return Task.FromResult(_connectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
        _connectionString = null;
        _initialized = false;
        GC.SuppressFinalize(this);
    }
} 