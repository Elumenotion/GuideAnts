namespace GuideAntsApi.Services.Bootstrap;

public interface IGuideAntsSystemSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}
