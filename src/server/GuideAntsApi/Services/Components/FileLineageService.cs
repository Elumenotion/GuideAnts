using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Core;

namespace GuideAntsApi.Services.Components;

/// <inheritdoc />
public class FileLineageService : IFileLineageService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public FileLineageService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }
    
    /// <summary>
    /// Creates a new scope and returns the DbContext. Use with 'using' statement.
    /// </summary>
    private IServiceScope CreateDbScope() => _scopeFactory.CreateScope();
    
    /// <summary>
    /// Gets the DbContext from a scope. Use with CreateDbScope().
    /// </summary>
    private static ApplicationDbContext GetDbContext(IServiceScope scope) => 
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    public async Task RecordAsync(
        FileKind fileKind,
        Guid projectId,
        Guid fileId,
        int? versionNumber,
        FileLineageAction action,
        Guid? notebookId,
        string storagePath,
        bool saveImmediately = true)
    {
        var userId = Guid.Empty.ToString();

        var entity = new FileLineageEvent
        {
            UserId = userId,
            FileKind = fileKind,
            ProjectId = projectId,
            FileId = fileId,
            VersionNumber = versionNumber,
            Action = action,
            NotebookId = notebookId,
            StoragePath = storagePath,
            Timestamp = DateTime.UtcNow
        };

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        context.FileLineageEvents.Add(entity);
        if (saveImmediately)
        {
            await context.SaveChangesAsync();
        }
    }
} 