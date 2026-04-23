using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides;

public interface IGuideExportImportService
{
    Task<byte[]> ExportGuideAsync(Guid guideId);
    Task<ImportGuideResultDto> ImportGuideAsync(Stream zipStream);
    Task<ImportPreviewDto> PreviewImportAsync(Stream zipStream);
    
    Task<byte[]> ExportAssistantAsync(Guid assistantId);
    Task<ImportGuideResultDto> ImportAssistantAsync(Stream zipStream);
}
