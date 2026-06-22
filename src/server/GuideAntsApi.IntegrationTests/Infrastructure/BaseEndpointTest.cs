using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using GuideAntsApi.DataModel;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

public abstract class BaseEndpointTest : BaseIntegrationTest
{
    protected override async Task CleanDatabaseAsync()
    {
        if (SharedFactory == null) return;

        using var scope = SharedFactory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Use SQL commands for cleaner deletion to avoid EF cascade issues
        // Delete in dependency order to avoid foreign key violations
        
        // First delete message edit histories (deepest level)
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE meh FROM MessageEditHistories meh 
              INNER JOIN NotebookConversationMessages ncm ON meh.MessageId = ncm.Id
              INNER JOIN NotebookConversations nc ON ncm.NotebookConversationId = nc.Id
              INNER JOIN Notebooks n ON nc.NotebookId = n.Id
              INNER JOIN Projects p ON n.ProjectId = p.Id
              ;");

        // Delete conversation messages
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE ncm FROM NotebookConversationMessages ncm 
              INNER JOIN NotebookConversations nc ON ncm.NotebookConversationId = nc.Id
              INNER JOIN Notebooks n ON nc.NotebookId = n.Id
              INNER JOIN Projects p ON n.ProjectId = p.Id
              ;");

        // Delete conversation turns  
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE ct FROM ConversationTurns ct 
              INNER JOIN NotebookConversations nc ON ct.NotebookConversationId = nc.Id
              INNER JOIN Notebooks n ON nc.NotebookId = n.Id
              INNER JOIN Projects p ON n.ProjectId = p.Id
              ;");

        // Delete file lineage events
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE fle FROM FileLineageEvents fle 
              INNER JOIN Notebooks n ON fle.NotebookId = n.Id
              INNER JOIN Projects p ON n.ProjectId = p.Id
              ;");

        // Delete notebook files
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE nf FROM NotebookFiles nf 
              INNER JOIN Notebooks n ON nf.NotebookId = n.Id
              INNER JOIN Projects p ON n.ProjectId = p.Id
              ;");

        // Delete notebook conversations
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE nc FROM NotebookConversations nc 
              INNER JOIN Notebooks n ON nc.NotebookId = n.Id
              INNER JOIN Projects p ON n.ProjectId = p.Id
              ;");


        // Delete content file versions FIRST because they have FK to ProjectFolders
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE cfv FROM ContentFileVersions cfv 
              INNER JOIN ContentFiles cf ON cfv.ContentFileId = cf.Id
              INNER JOIN Projects p ON cf.ProjectId = p.Id
              ;");

        // Delete content files
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE cf FROM ContentFiles cf 
              INNER JOIN Projects p ON cf.ProjectId = p.Id
              ;");
              
        // Delete project folders
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE pf FROM ProjectFolders pf 
              INNER JOIN Projects p ON pf.ProjectId = p.Id
              ;");

        // Delete host folder mount links and mounts
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE hfl FROM HostFolderMountLinks hfl
              INNER JOIN HostFolderMounts hfm ON hfl.HostFolderMountId = hfm.Id
              INNER JOIN Projects p ON hfm.ProjectId = p.Id
              ;");

        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE hfm FROM HostFolderMounts hfm
              INNER JOIN Projects p ON hfm.ProjectId = p.Id
              ;");

        // Delete published guides before notebooks (FK_PublishedGuides_Notebooks_NotebookId).
        // The system-guide seeder publishes the seeded guides, so these rows reference
        // system-project notebooks and would block the notebook delete below.
        await dbContext.Database.ExecuteSqlRawAsync(@"DELETE FROM PublishedGuides;");

        // Delete notebooks
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE n FROM Notebooks n 
              INNER JOIN Projects p ON n.ProjectId = p.Id
              ;");

        // Delete links
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE l FROM Links l 
              INNER JOIN Projects p ON l.ProjectId = p.Id
              ;");

        // Delete semi-structured data
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE ssd FROM SemiStructuredProjectDatas ssd 
              INNER JOIN Projects p ON ssd.ProjectId = p.Id
              ;");

        // Delete projects
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE FROM Projects;");

        // Delete test users
        await dbContext.Database.ExecuteSqlRawAsync(
            @"DELETE FROM Users 
              WHERE Email LIKE '%@example.com%' OR Email LIKE '%test%'");
    }
} 