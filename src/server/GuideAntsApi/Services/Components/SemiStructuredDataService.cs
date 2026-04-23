using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Core;

namespace GuideAntsApi.Services.Components;

public interface ISemiStructuredDataService : IProjectComponentService<SemiStructuredProjectDataDto>
{
}

public class SemiStructuredDataService(IServiceScopeFactory scopeFactory) : ISemiStructuredDataService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    /// <summary>
    /// Creates a new scope and returns the DbContext. Use with 'using' statement.
    /// </summary>
    private IServiceScope CreateDbScope() => _scopeFactory.CreateScope();

    /// <summary>
    /// Gets the DbContext from a scope. Use with CreateDbScope().
    /// </summary>
    private static ApplicationDbContext GetDbContext(IServiceScope scope) => 
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    private static SemiStructuredProjectDataDto ToSemiStructuredDataDto(SemiStructuredProjectData data)
    {
        return new SemiStructuredProjectDataDto(data.Id, data.Placeholder);
    }

    public async Task<SemiStructuredProjectDataDto> CreateAsync(Guid projectId, object createDto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var placeholder = createDto.ToString() ?? throw new ArgumentNullException(nameof(createDto));

        var data = new SemiStructuredProjectData
        {
            ProjectId = projectId,
            Placeholder = placeholder
        };

        context.SemiStructuredProjectDatas.Add(data);
        await context.SaveChangesAsync();

        return ToSemiStructuredDataDto(data);
    }

    public async Task<SemiStructuredProjectDataDto?> UpdateAsync(Guid projectId, Guid componentId, object updateDto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var placeholder = updateDto.ToString() ?? throw new ArgumentNullException(nameof(updateDto));

        var data = await context.SemiStructuredProjectDatas
            .FirstOrDefaultAsync(d => d.Id == componentId && d.ProjectId == projectId);

        if (data == null)
        {
            return null;
        }

        data.Placeholder = placeholder;
        await context.SaveChangesAsync();

        return ToSemiStructuredDataDto(data);
    }

    public async Task<bool> DeleteAsync(Guid projectId, Guid componentId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var data = await context.SemiStructuredProjectDatas
            .FirstOrDefaultAsync(d => d.Id == componentId && d.ProjectId == projectId);

        if (data == null)
        {
            return false;
        }

        context.SemiStructuredProjectDatas.Remove(data);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<SemiStructuredProjectDataDto?> GetAsync(Guid projectId, Guid componentId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var data = await context.SemiStructuredProjectDatas
            .FirstOrDefaultAsync(d => d.Id == componentId && d.ProjectId == projectId);

        return data == null ? null : ToSemiStructuredDataDto(data);
    }

    public async Task<IEnumerable<SemiStructuredProjectDataDto>> GetAllForProjectAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var allData = await context.SemiStructuredProjectDatas
            .Where(d => d.ProjectId == projectId)
            .ToListAsync();

        return allData.Select(ToSemiStructuredDataDto);
    }
} 