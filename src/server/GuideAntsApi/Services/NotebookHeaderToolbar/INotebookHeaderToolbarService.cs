using GuideAntsApi.Models;

namespace GuideAntsApi.Services.NotebookHeaderToolbar;

public interface INotebookHeaderToolbarService
{
    /// <param name="notebookId">Notebook for notebook-scoped runtime (llama) context.</param>
    /// <param name="conversationId">When set, chat segment reflects this conversation's effective model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<NotebookHeaderToolbarDto> GetToolbarAsync(Guid notebookId, Guid? conversationId, CancellationToken cancellationToken = default);
}
