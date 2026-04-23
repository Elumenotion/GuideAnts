SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @RetainedUserId uniqueidentifier = 'fd787545-ffae-4ea9-81fa-700db2fffccd';

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    THROW 51010, 'Precheck failed: dbo.Users table not found.', 1;
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Id = @RetainedUserId)
BEGIN
    THROW 51011, 'Precheck failed: retained user was not found.', 1;
END;

IF OBJECT_ID(N'tempdb..#RetainedProjects', N'U') IS NOT NULL DROP TABLE #RetainedProjects;
CREATE TABLE #RetainedProjects
(
    ProjectId uniqueidentifier NOT NULL PRIMARY KEY
);

IF OBJECT_ID(N'dbo.ProjectUserRoles', N'U') IS NOT NULL
BEGIN
    INSERT INTO #RetainedProjects(ProjectId)
    SELECT DISTINCT pur.ProjectId
    FROM dbo.ProjectUserRoles pur
    WHERE pur.UserId = @RetainedUserId;
END;

IF OBJECT_ID(N'dbo.Teams', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.Projects', N'U') IS NOT NULL
BEGIN
    INSERT INTO #RetainedProjects(ProjectId)
    SELECT DISTINCT p.Id
    FROM dbo.Projects p
    INNER JOIN dbo.Teams t ON t.Id = p.TeamId
    WHERE t.CreatedByUserId = @RetainedUserId
      AND NOT EXISTS (SELECT 1 FROM #RetainedProjects rp WHERE rp.ProjectId = p.Id);
END;

SELECT
    UserId = u.Id,
    u.Name,
    u.Email,
    u.Created
FROM dbo.Users u
WHERE u.Id = @RetainedUserId;

SELECT
    RetainedProjectCount = COUNT(*)
FROM #RetainedProjects;

SELECT
    TableName = N'Users',
    RowCount = COUNT_BIG(*)
FROM dbo.Users
UNION ALL
SELECT N'Projects', COUNT_BIG(*) FROM dbo.Projects
UNION ALL
SELECT N'ProjectUserRoles', COUNT_BIG(*) FROM dbo.ProjectUserRoles
UNION ALL
SELECT N'Teams', COUNT_BIG(*) FROM dbo.Teams
UNION ALL
SELECT N'TeamMemberships', COUNT_BIG(*) FROM dbo.TeamMemberships
UNION ALL
SELECT N'TeamInvitations', COUNT_BIG(*) FROM dbo.TeamInvitations
UNION ALL
SELECT N'Notebooks', COUNT_BIG(*) FROM dbo.Notebooks
UNION ALL
SELECT N'NotebookConversations', COUNT_BIG(*) FROM dbo.NotebookConversations
UNION ALL
SELECT N'NotebookConversationMessages', COUNT_BIG(*) FROM dbo.NotebookConversationMessages
UNION ALL
SELECT N'ContentFiles', COUNT_BIG(*) FROM dbo.ContentFiles
UNION ALL
SELECT N'UsageEvents', COUNT_BIG(*) FROM dbo.UsageEvents
UNION ALL
SELECT N'UsageReportCategories', COUNT_BIG(*) FROM dbo.UsageReportCategories
UNION ALL
SELECT N'UsageReportCategoryOperations', COUNT_BIG(*) FROM dbo.UsageReportCategoryOperations
ORDER BY TableName;

SELECT
    p.Id,
    p.Title,
    p.TeamId,
    p.Created
FROM dbo.Projects p
INNER JOIN #RetainedProjects rp ON rp.ProjectId = p.Id
ORDER BY p.Created DESC;
