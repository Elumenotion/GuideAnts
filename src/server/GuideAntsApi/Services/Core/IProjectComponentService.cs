namespace GuideAntsApi.Services.Core;

public interface IProjectComponentService<TDto>
{
    Task<TDto> CreateAsync(Guid projectId, object createDto);
    Task<TDto?> UpdateAsync(Guid projectId, Guid componentId, object updateDto);
    Task<bool> DeleteAsync(Guid projectId, Guid componentId);
    Task<TDto?> GetAsync(Guid projectId, Guid componentId);
    Task<IEnumerable<TDto>> GetAllForProjectAsync(Guid projectId);
} 