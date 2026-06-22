using GuideAntsApi.Endpoints;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsHuggingFaceEndpoints
{
    public static void MapSettingsHuggingFaceEndpoints(this WebApplication app)
    {
        var huggingFaceGroup = SettingsGroupFactory.MapHuggingFaceGroup(app);

        huggingFaceGroup.MapGet("/repositories/{owner}/{repo}/files", (
            string owner,
            string repo,
            HttpRequest request,
            GuideAntsApi.Services.HuggingFace.IHuggingFaceRepositoryBrowser browser,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            HuggingFaceBrowseHandler.ExecuteAsync(
                owner,
                repo,
                request,
                browser,
                loggerFactory,
                cancellationToken))
        .WithName("BrowseHuggingFaceRepository")
        .Produces<GuideAntsApi.Services.HuggingFace.HuggingFaceRepositoryListing>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status502BadGateway);
    }
}
