using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using GuideAntsApi.DataModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AntRunner.Chat.Abstractions;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

public class TestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    private bool _initialized = false;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!_initialized)
        {
            await TestContainerManager.Instance.EnsureInitializedAsync();
            _connectionString = await TestContainerManager.Instance.GetConnectionStringAsync();
            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                throw new InvalidOperationException("Test SQL connection string was not initialized.");
            }
            _initialized = true;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException(
                "TestWebApplicationFactory was not initialized with a SQL connection string. " +
                "Call InitializeAsync() before creating clients.");
        }

        // Force test container connection string to win in every config access path.
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _connectionString);

        builder.ConfigureAppConfiguration((context, config) =>
        {
            var testProjectDir = GetTestProjectDirectory();
            var configPath = Path.GetFullPath(Path.Combine(testProjectDir, "..", "GuideAntsApi"));

            config.AddJsonFile(Path.Combine(configPath, "appsettings.json"), optional: false, reloadOnChange: true);
            config.AddJsonFile(Path.Combine(configPath, $"appsettings.{context.HostingEnvironment.EnvironmentName}.json"), optional: true, reloadOnChange: true);
            
            // Add test-specific configuration to reduce logging noise
            config.AddJsonFile(Path.Combine(testProjectDir, "appsettings.test.json"), optional: true, reloadOnChange: true);
            
            config.AddEnvironmentVariables();
            
            // Allow derived classes to add their own config before the final in-memory collection
            ConfigureTestAppConfiguration(config, context);

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["KernelMemory:BaseUrl"] = "http://localhost:9001",
                ["NOTEBOOK_TEMPLATES_BASE_FOLDER_PATH"] = Path.GetFullPath(Path.Combine(testProjectDir, "..", "NotebookTemplates"))
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove the existing db context registration
            var descriptor = services.SingleOrDefault(d => 
                d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));

            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            // Add DB context using container connection
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlServer(_connectionString), 
                contextLifetime: ServiceLifetime.Scoped, 
                optionsLifetime: ServiceLifetime.Singleton);

            // Integration tests should not depend on external LLM providers.
            services.RemoveAll<IChatCompletionClientFactory>();
            services.AddSingleton<IChatCompletionClientFactory, FakeChatCompletionClientFactory>();
        });
    }

    private static string GetTestProjectDirectory()
    {
        var currentDirectory = AppContext.BaseDirectory;
        while (currentDirectory != null)
        {
            var projectFile = Directory.GetFiles(currentDirectory, "GuideAntsApi.IntegrationTests.csproj").FirstOrDefault();
            if (projectFile != null)
            {
                return currentDirectory;
            }
            currentDirectory = Directory.GetParent(currentDirectory)?.FullName;
        }
        throw new Exception("Could not find the test project directory.");
    }

    protected virtual void ConfigureTestAppConfiguration(IConfigurationBuilder config, WebHostBuilderContext context)
    {
        // Base implementation does nothing, derived classes can override.
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }
} 
