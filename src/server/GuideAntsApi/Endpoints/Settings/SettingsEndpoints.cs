namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapSettingsCoreEndpoints();
        app.MapSettingsModelsEndpoints();
        app.MapSettingsRuntimeProfilesEndpoints();
        app.MapSettingsServiceEditorEndpoints();
        app.MapSettingsServiceLocalModelsEndpoints();
        app.MapSettingsRoutingEndpoints();
        app.MapSettingsOverviewEndpoints();
        app.MapSettingsInfrastructureEndpoints();
        app.MapSettingsLlamaEndpoints();
        app.MapSettingsHuggingFaceEndpoints();
    }
}
