using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides;

public interface ICatalogService
{
    Task<IEnumerable<ModelDto>> GetModelsAsync();
    Task<IEnumerable<ToolDto>> GetToolsAsync();
    Task<IEnumerable<GlobalAssistantDto>> GetGlobalAssistantsAsync();
    Task<GlobalAssistantDetailsDto?> GetGlobalAssistantAsync(Guid id);
    Task<byte[]?> GetGlobalAssistantAvatarBytesAsync(Guid id);
}

