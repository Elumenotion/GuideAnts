using Microsoft.EntityFrameworkCore;
using CliWrap;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Components;

public interface INotebookCopyService
{
    Task<NotebookDto> CopyAsync(Guid projectId, CopyNotebookDto dto);
}

public class NotebookCopyService(IServiceScopeFactory scopeFactory) : INotebookCopyService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    
    /// <summary>
    /// Creates a new scope and returns the DbContext. Use with 'using' statement.
    /// </summary>
    private IServiceScope CreateDbScope() => _scopeFactory.CreateScope();
    
    /// <summary>
    /// Gets the DbContext from a scope. Use with CreateDbScope().
    /// </summary>
    private static ApplicationDbContext GetDbContext(IServiceScope scope) => 
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    private static NotebookDto ToDto(Notebook nb)
    {
        // Use GuideId if set, otherwise use NotebookTemplateId (legacy)
        var guideId = nb.GuideId ?? nb.NotebookTemplateId ?? throw new InvalidOperationException("Notebook must have either GuideId or NotebookTemplateId");
        return new(nb.Id, nb.Title, nb.Description, guideId, nb.HomePageFileId, nb.Created);
    }

    public async Task<NotebookDto> CopyAsync(Guid projectId, CopyNotebookDto dto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var sourceNotebook = await context.Notebooks
            .Include(n => n.Conversations)
                .ThenInclude(c => c.Messages)
            .FirstOrDefaultAsync(n => n.Id == dto.SourceNotebookId && n.ProjectId == projectId);

        if (sourceNotebook == null)
            throw new ArgumentException("Source notebook not found");

        // Determine which conversation (use first by Created as legacy behaviour)
        var sourceConversation = sourceNotebook.Conversations
            .OrderBy(c => c.Created)
            .FirstOrDefault();

        List<NotebookConversationMessage> messagesToCopy = [];
        if (sourceConversation != null)
        {
            if (dto.SourceConversationMessageId.HasValue)
            {
                var cutoff = sourceConversation.Messages.FirstOrDefault(x => x.Id == dto.SourceConversationMessageId.Value)?.Created;
                if (cutoff.HasValue)
                    messagesToCopy = sourceConversation.Messages
                        .Where(m => m.Created <= cutoff.Value)
                        .OrderBy(m => m.Created)
                        .ToList();
            }
            else
            {
                messagesToCopy = sourceConversation.Messages.OrderBy(m => m.Created).ToList();
            }
        }

        var newNotebook = new Notebook
        {
            ProjectId = projectId,
            Title = dto.Title,
            Slug = await GenerateUniqueNotebookSlugAsync(context, projectId, dto.Title),
            Description = sourceNotebook.Description, // Copy description from source
            GuideId = sourceNotebook.GuideId,
            SourceNotebookId = sourceNotebook.Id,
            SourceConversationMessageId = dto.SourceConversationMessageId
        };

        var newConversation = new NotebookConversation
        {
            Notebook = newNotebook,
            Title = sourceConversation?.Title ?? "Conversation 1",
            Messages = messagesToCopy.Select(m => new NotebookConversationMessage
            {
                Role = m.Role,
                Content = m.Content,
                UserId = m.UserId,
                AssistantName = m.AssistantName,
                ModelDeploymentId = m.ModelDeploymentId,
                ToolCallId = m.ToolCallId,
                FunctionName = m.FunctionName,
                ToolCalls = m.ToolCalls,
                Created = m.Created
            }).ToList()
        };

        context.Notebooks.Add(newNotebook);
        context.NotebookConversations.Add(newConversation);
        await context.SaveChangesAsync();

        // Copy notebook files (preserve relative paths) and recreate Output/ symlinks if present
        var sourceFiles = await context.NotebookFiles
            .Where(f => f.NotebookId == sourceNotebook.Id)
            .ToListAsync();

        if (sourceFiles.Count > 0)
        {
            var pathResolver = scope.ServiceProvider.GetRequiredService<IStoragePathResolver>();

            string GetRoot(Guid nbId) => pathResolver.GetNotebookRootPath(projectId, nbId);
            var srcRoot = GetRoot(sourceNotebook.Id);
            var dstRoot = GetRoot(newNotebook.Id);
            Directory.CreateDirectory(dstRoot);
            Directory.CreateDirectory(Path.Combine(dstRoot, "Output"));

            foreach (var sf in sourceFiles)
            {
                // Skip Output/ entries (legacy) and rebuild only from Resources/ data
                if (sf.RelativePath.StartsWith("Output/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var srcPath = Path.Combine(srcRoot, sf.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
                var dstPath = Path.Combine(dstRoot, sf.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                if (File.Exists(srcPath))
                {
                    File.Copy(srcPath, dstPath, overwrite: true);
                }

                var fi = new FileInfo(dstPath);
                var nf = new NotebookFile
                {
                    NotebookId = newNotebook.Id,
                    RelativePath = sf.RelativePath,
                    FileSize = fi.Exists ? fi.Length : sf.FileSize,
                    LastModifiedUtc = fi.Exists ? fi.LastWriteTimeUtc : DateTime.UtcNow,
                    FileHash = fi.Exists ? ComputeSha256(dstPath) : sf.FileHash,
                    OriginContentFileVersionId = sf.OriginContentFileVersionId,
                    Created = DateTime.UtcNow
                };
                nf.GenerateDocumentId(newNotebook.Id);
                context.NotebookFiles.Add(nf);

                // Recreate symlink in Output/ only for Resources/ files
                if (sf.RelativePath.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase) ||
                    sf.RelativePath.StartsWith("Resources\\", StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(sf.RelativePath);
                    var symlinkPath = Path.Combine(dstRoot, "Output", fileName);
                    var targetRelative = $"../{sf.RelativePath.Replace("\\", "/")}";
                    await CreateSymlinkAsync(symlinkPath, targetRelative, scope);
                }
            }

            await context.SaveChangesAsync();
        }

        return ToDto(newNotebook);
    }

    private static async Task<string> GenerateUniqueNotebookSlugAsync(ApplicationDbContext context, Guid projectId, string title)
    {
        var baseSlug = SlugGenerator.Generate(title);
        var slug = baseSlug;
        var suffix = 2;
        while (await context.Notebooks.AnyAsync(n => n.ProjectId == projectId && n.Slug == slug))
        {
            slug = SlugGenerator.AddNumericSuffix(baseSlug, suffix++);
        }

        return slug;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static async Task CreateSymlinkAsync(string symlinkPath, string targetRelative, IServiceScope scope)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (File.Exists(symlinkPath) || Directory.Exists(symlinkPath))
                {
                    try { File.Delete(symlinkPath); } catch { }
                }
                var result = await Cli.Wrap("ln")
                    .WithArguments(new[] { "-sf", targetRelative, symlinkPath })
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteAsync();
                // best-effort: ignore non-zero exit
            }
            else
            {
                // best-effort: warn via Console (logger not injected here)
                Console.WriteLine($"[WARN] Symlink creation skipped (unsupported platform) for {symlinkPath}");
            }
        }
        catch (Exception)
        {
            // best-effort: swallow errors to avoid failing duplication
        }
    }
} 
