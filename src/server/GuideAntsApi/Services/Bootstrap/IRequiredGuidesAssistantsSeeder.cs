namespace GuideAntsApi.Services.Bootstrap;

public interface IRequiredGuidesAssistantsSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}
