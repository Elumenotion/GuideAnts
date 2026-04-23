using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Core;

namespace GuideAntsApi.Services.Components;

public interface ILinkService : IProjectComponentService<LinkDto>
{
}

public class LinkService(IServiceScopeFactory scopeFactory) : ILinkService
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

    private static LinkDto ToLinkDto(Link link)
    {
        return new LinkDto(link.Id, link.Url);
    }

    public async Task<LinkDto> CreateAsync(Guid projectId, object createDto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var url = createDto.ToString() ?? throw new ArgumentNullException(nameof(createDto));

        var link = new Link
        {
            ProjectId = projectId,
            Url = url
        };

        context.Links.Add(link);
        await context.SaveChangesAsync();

        return ToLinkDto(link);
    }

    public async Task<LinkDto?> UpdateAsync(Guid projectId, Guid componentId, object updateDto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var url = updateDto.ToString() ?? throw new ArgumentNullException(nameof(updateDto));

        var link = await context.Links
            .FirstOrDefaultAsync(l => l.Id == componentId && l.ProjectId == projectId);

        if (link == null)
        {
            return null;
        }

        link.Url = url;
        await context.SaveChangesAsync();

        return ToLinkDto(link);
    }

    public async Task<bool> DeleteAsync(Guid projectId, Guid componentId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var link = await context.Links
            .FirstOrDefaultAsync(l => l.Id == componentId && l.ProjectId == projectId);

        if (link == null)
        {
            return false;
        }

        context.Links.Remove(link);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<LinkDto?> GetAsync(Guid projectId, Guid componentId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var link = await context.Links
            .FirstOrDefaultAsync(l => l.Id == componentId && l.ProjectId == projectId);

        return link == null ? null : ToLinkDto(link);
    }

    public async Task<IEnumerable<LinkDto>> GetAllForProjectAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var links = await context.Links
            .Where(l => l.ProjectId == projectId)
            .ToListAsync();

        return links.Select(ToLinkDto);
    }
} 