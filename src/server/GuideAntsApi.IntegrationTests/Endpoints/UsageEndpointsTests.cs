using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class UsageEndpointsTests : BaseEndpointTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        SetupAuthentication();
        await SeedUsageAsync();
    }

    [TestMethod]
    public async Task Usage_summary_by_project_details_and_breakdown_return_data()
    {
        var from = DateTime.UtcNow.AddDays(-2).ToString("O");
        var to = DateTime.UtcNow.AddDays(1).ToString("O");

        var summaryResponse = await Client.GetAsync($"/api/usage/summary?from={from}&to={to}&bucket=Day");
        summaryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        summary.GetProperty("totals").GetProperty("totalEvents").GetInt64().Should().BeGreaterThan(0);

        var byProjectResponse = await Client.GetAsync($"/api/usage/by-project?from={from}&to={to}");
        byProjectResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var byProject = await byProjectResponse.Content.ReadFromJsonAsync<List<ProjectUsageSummaryDto>>();
        byProject.Should().NotBeNull();
        byProject!.Should().NotBeEmpty();

        var projectId = byProject[0].ProjectId;
        var projectSummaryResponse = await Client.GetAsync(
            $"/api/usage/projects/{projectId}/summary?from={from}&to={to}&bucket=Day");
        projectSummaryResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var detailsResponse = await Client.GetAsync(
            $"/api/usage/details?from={from}&to={to}&page=1&pageSize=20");
        detailsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var details = await detailsResponse.Content.ReadFromJsonAsync<PagedResultDto<UsageEventDto>>();
        details!.Items.Should().NotBeEmpty();

        var breakdownResponse = await Client.GetAsync($"/api/usage/breakdown?from={from}&to={to}");
        breakdownResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var breakdown = await breakdownResponse.Content.ReadFromJsonAsync<UsageBreakdownWithCategoriesDto>();
        breakdown!.ByService.Should().NotBeEmpty();
    }

    private async Task SeedUsageAsync()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Title = "Usage Test Project",
            Slug = $"usage-{projectId:N}",
            Created = DateTime.UtcNow
        });

        db.UsageEvents.Add(new UsageEvent
        {
            ProjectId = projectId,
            Category = UsageCategory.ChatCompletion,
            Service = "AzureOpenAI",
            Operation = "chat",
            Created = DateTime.UtcNow.AddHours(-3),
            ChargeUsd = 1.5m,
            ValueInput = 100,
            ValueOutput = 50
        });
        await db.SaveChangesAsync();
    }
}
