using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Tests.TestUtils;

internal sealed class TestServiceScopeFactory : IServiceScopeFactory
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IConfiguration? _configuration;

    public TestServiceScopeFactory(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public TestServiceScopeFactory(ApplicationDbContext dbContext, IConfiguration configuration)
    {
        _dbContext = dbContext;
        _configuration = configuration;
    }

    public IServiceScope CreateScope()
    {
        return new TestServiceScope(_dbContext, _configuration);
    }
}

internal sealed class TestServiceScope : IServiceScope
{
    public IServiceProvider ServiceProvider { get; }

    public TestServiceScope(ApplicationDbContext dbContext, IConfiguration? configuration)
    {
        ServiceProvider = new TestServiceProvider(dbContext, configuration);
    }

    public void Dispose()
    {
        // Do not dispose the provided DbContext here; the test owns its lifecycle
    }
}

internal sealed class TestServiceProvider : IServiceProvider
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IConfiguration? _configuration;

    public TestServiceProvider(ApplicationDbContext dbContext, IConfiguration? configuration)
    {
        _dbContext = dbContext;
        _configuration = configuration;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ApplicationDbContext))
        {
            return _dbContext;
        }

        if (serviceType == typeof(IConfiguration) && _configuration != null)
        {
            return _configuration;
        }

        if (serviceType == typeof(ISystemGuideCatalogFilter))
        {
            return EmptySystemGuideCatalogFilter.Instance;
        }

        return null;
    }
}

internal sealed class EmptySystemGuideCatalogFilter : ISystemGuideCatalogFilter
{
    public static readonly EmptySystemGuideCatalogFilter Instance = new();

    public Task<IReadOnlySet<Guid>> GetHiddenGuideIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    public Task<IReadOnlySet<Guid>> GetHiddenGuideIdsForCatalogAsync(
        Guid? projectId,
        CancellationToken cancellationToken = default) =>
        GetHiddenGuideIdsAsync(cancellationToken);
}


