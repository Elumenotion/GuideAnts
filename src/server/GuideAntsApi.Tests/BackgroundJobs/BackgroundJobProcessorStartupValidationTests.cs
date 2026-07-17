using System.Reflection;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class BackgroundJobProcessorStartupValidationTests
{
    [TestMethod]
    public async Task InitializeJobHandlers_ThrowsWhenHandlerJobTypeMissingFromOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJobHandler>(new StubJobHandler("OrphanHandler"));
        services.AddSingleton<IOptions<JobProcessorOptions>>(OptionsFactory.Create(new JobProcessorOptions
        {
            JobTypes = new Dictionary<string, JobTypeOptions>
            {
                ["ConfiguredHandler"] = new() { MaxConcurrency = 1 }
            }
        }));
        services.AddSingleton<IOptions<JobRetryOptions>>(OptionsFactory.Create(new JobRetryOptions()));
        services.AddSingleton(Mock.Of<IActiveJobExecutionRegistry>());

        var provider = services.BuildServiceProvider();
        var processor = new BackgroundJobProcessor(
            provider,
            provider.GetRequiredService<IOptions<JobProcessorOptions>>(),
            provider.GetRequiredService<IOptions<JobRetryOptions>>(),
            provider.GetRequiredService<IActiveJobExecutionRegistry>(),
            NullLogger<BackgroundJobProcessor>.Instance);

        var method = typeof(BackgroundJobProcessor).GetMethod(
            "InitializeJobHandlersAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var initialize = async () => await (Task)method!.Invoke(processor, null)!;
        var ex = await Assert.ThrowsExactlyAsync<TargetInvocationException>(initialize);
        ex.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("OrphanHandler");
    }

    private sealed class StubJobHandler(string jobType) : IJobHandler
    {
        public string JobType { get; } = jobType;

        public Task<JobExecutionResult> HandleAsync(string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(JobExecutionResult.Success());
    }
}
