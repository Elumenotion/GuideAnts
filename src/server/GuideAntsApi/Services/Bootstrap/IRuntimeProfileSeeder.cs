namespace GuideAntsApi.Services.Bootstrap;

public interface IRuntimeProfileSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}
