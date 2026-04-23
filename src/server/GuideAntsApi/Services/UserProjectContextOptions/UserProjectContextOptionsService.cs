using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.UserProjectContextOptions
{
    public class UserProjectContextOptionsService : IUserProjectContextOptionsService
    {
        private readonly ApplicationDbContext _context;

        public UserProjectContextOptionsService(ApplicationDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task UpsertAsync(Guid userId, Guid projectId, IEnumerable<KeyValuePair<string, string>> options)
        {
            foreach (var (key, value) in options)
            {
                var entity = await _context.UserProjectContextOptions
                    .FindAsync(userId, projectId, key);

                if (entity == null)
                {
                    _context.UserProjectContextOptions.Add(new UserProjectContextOption
                    {
                        UserId = userId,
                        ProjectId = projectId,
                        Key = key,
                        Value = value
                    });
                }
                else
                {
                    entity.Value = value;
                }
            }

            await _context.SaveChangesAsync();
        }

        public async Task<Dictionary<string, string>> GetOptionsAsync(Guid userId, Guid projectId)
        {
            return await _context.UserProjectContextOptions
                .Where(o => o.UserId == userId && o.ProjectId == projectId)
                .ToDictionaryAsync(o => o.Key, o => o.Value);
        }
    }
}


