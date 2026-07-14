using AntRunner.Chat.LlamaCpp;
using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LlamaInferenceTimeoutRecoveryServiceTests
{
    private const string Alias = "qwen3.5-27b";

    [TestMethod]
    public async Task RequestRecoveryAsync_UnloadsConfirmsAndReloadsAlias()
    {
        var runtime = new Mock<ILlamaServerRuntimeClient>(MockBehavior.Strict);
        runtime.Setup(client => client.UnloadModelAsync(Alias, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        runtime.SetupSequence(client => client.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Models("unloaded"))
            .ReturnsAsync(Models("loaded"));
        runtime.Setup(client => client.LoadModelAsync(Alias, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var admin = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        var service = CreateService(runtime.Object, admin.Object);

        var result = await service.RequestRecoveryAsync(Alias, timeoutSeconds: 300)
            .WaitAsync(TimeSpan.FromSeconds(2));

        result.Succeeded.Should().BeTrue();
        result.EscalatedToServerRestart.Should().BeFalse();
        runtime.Verify(client => client.UnloadModelAsync(Alias, It.IsAny<CancellationToken>()), Times.Once);
        runtime.Verify(client => client.LoadModelAsync(Alias, It.IsAny<CancellationToken>()), Times.Once);
        admin.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task RequestRecoveryAsync_EscalatesToServerRestart_WhenModelUnloadFails()
    {
        var runtime = new Mock<ILlamaServerRuntimeClient>(MockBehavior.Strict);
        runtime.Setup(client => client.UnloadModelAsync(Alias, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("router did not respond"));
        runtime.Setup(client => client.LoadModelAsync(Alias, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        runtime.Setup(client => client.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Models("loaded"));

        var admin = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        admin.Setup(client => client.RestartLlamaServerAsync(CancellationToken.None))
            .ReturnsAsync(new LlamaAdminRestartResultDto(
                Restarted: true,
                Termed: true,
                OldPid: 10,
                NewPid: 20));
        var service = CreateService(runtime.Object, admin.Object);

        var result = await service.RequestRecoveryAsync(Alias, timeoutSeconds: 300)
            .WaitAsync(TimeSpan.FromSeconds(2));

        result.Succeeded.Should().BeTrue();
        result.EscalatedToServerRestart.Should().BeTrue();
        admin.Verify(client => client.RestartLlamaServerAsync(CancellationToken.None), Times.Once);
        runtime.Verify(client => client.LoadModelAsync(Alias, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RequestRecoveryAsync_JoinsConcurrentRecoveryForSameAlias()
    {
        var unloadRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Mock<ILlamaServerRuntimeClient>(MockBehavior.Strict);
        runtime.Setup(client => client.UnloadModelAsync(Alias, It.IsAny<CancellationToken>()))
            .Returns(unloadRelease.Task);
        runtime.SetupSequence(client => client.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Models("unloaded"))
            .ReturnsAsync(Models("loaded"));
        runtime.Setup(client => client.LoadModelAsync(Alias, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var admin = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        var service = CreateService(runtime.Object, admin.Object);

        var first = service.RequestRecoveryAsync(Alias, timeoutSeconds: 300);
        var second = service.RequestRecoveryAsync(Alias, timeoutSeconds: 300);

        second.Should().BeSameAs(first);
        unloadRelease.SetResult();
        (await first.WaitAsync(TimeSpan.FromSeconds(2))).Succeeded.Should().BeTrue();
        runtime.Verify(client => client.UnloadModelAsync(Alias, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EnsureInferenceAvailableAsync_KeepsCircuitOpenAfterFailedEscalation()
    {
        var runtime = new Mock<ILlamaServerRuntimeClient>(MockBehavior.Strict);
        runtime.Setup(client => client.UnloadModelAsync(Alias, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unload failed"));
        runtime.Setup(client => client.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Models("unloaded"));
        var admin = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        admin.Setup(client => client.RestartLlamaServerAsync(CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("restart failed"));
        var service = CreateService(runtime.Object, admin.Object);

        var result = await service.RequestRecoveryAsync(Alias, timeoutSeconds: 300)
            .WaitAsync(TimeSpan.FromSeconds(2));
        result.Succeeded.Should().BeFalse();

        var act = async () => await service.EnsureInferenceAvailableAsync(Alias);
        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.Reason.Should().Be(LlamaRuntimeCrashReason.Crashed);
    }

    private static LlamaInferenceTimeoutRecoveryService CreateService(
        ILlamaServerRuntimeClient runtimeClient,
        ILlamaRuntimeAdminClient adminClient)
    {
        return new LlamaInferenceTimeoutRecoveryService(
            runtimeClient,
            adminClient,
            new LlamaRuntimeCoordinator(),
            Microsoft.Extensions.Options.Options.Create(new LlamaInferenceTimeoutRecoveryOptions
            {
                UnloadTimeoutSeconds = 2,
                LoadTimeoutSeconds = 2,
                PollIntervalMilliseconds = 10
            }),
            NullLogger<LlamaInferenceTimeoutRecoveryService>.Instance);
    }

    private static LlamaModelsResponse Models(string state) =>
        new()
        {
            Data =
            [
                new LlamaModelData
                {
                    Id = Alias,
                    Status = new LlamaModelStatus { Value = state },
                    Failed = false
                }
            ]
        };
}
