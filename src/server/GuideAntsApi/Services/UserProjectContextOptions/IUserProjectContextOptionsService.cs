namespace GuideAntsApi.Services.UserProjectContextOptions
{
    public interface IUserProjectContextOptionsService
    {
        Task UpsertAsync(Guid userId, Guid projectId, IEnumerable<KeyValuePair<string, string>> options);
        Task<Dictionary<string, string>> GetOptionsAsync(Guid userId, Guid projectId);
    }
}


