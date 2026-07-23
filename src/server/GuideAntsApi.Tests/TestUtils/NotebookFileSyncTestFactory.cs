using GuideAntsApi.BackgroundJobs.Sync;
using GuideAntsApi.Services.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.TestUtils;

internal static class NotebookFileSyncTestFactory
{
    internal static NotebookFileSyncService Create(
        IServiceScopeFactory scopeFactory,
        INotebookFileReconciler? reconciler = null)
    {
        reconciler ??= new Mock<INotebookFileReconciler>().Object;
        return new NotebookFileSyncService(
            reconciler,
            scopeFactory,
            NullLogger<NotebookFileSyncService>.Instance);
    }
}
