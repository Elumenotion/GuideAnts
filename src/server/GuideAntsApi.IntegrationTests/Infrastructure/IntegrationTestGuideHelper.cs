using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.SystemGuide;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

internal static class IntegrationTestGuideHelper
{
    public static async Task<Guid> GetDefaultGuideIdAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;
        var db = scopedServices.GetRequiredService<ApplicationDbContext>();
        var catalogFilter = scopedServices.GetRequiredService<ISystemGuideCatalogFilter>();
        var hiddenGuideIds = await catalogFilter.GetHiddenGuideIdsAsync();

        var guideId = await db.Assistants
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive && !hiddenGuideIds.Contains(a.Id))
            .OrderBy(a => a.Name)
            .Select(a => a.Id)
            .FirstOrDefaultAsync();

        if (guideId == Guid.Empty)
        {
            var guide = new Assistant
            {
                Id = Guid.NewGuid(),
                Name = "Test Guide",
                Kind = AssistantKind.Guide,
                IsActive = true,
                IsGlobal = true
            };
            db.Assistants.Add(guide);
            await db.SaveChangesAsync();
            guideId = guide.Id;
        }

        return guideId;
    }
}
