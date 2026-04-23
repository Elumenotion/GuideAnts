SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @BaselineMigrationId nvarchar(150) = N'20260325193437_LiteBaselineV1';
DECLARE @BaselineProductVersion nvarchar(32) = N'9.0.11';
DECLARE @ExpectedLegacyHead nvarchar(150) = N'20260310231555_AddLlamaCppReasoningEffortString';
DECLARE @BridgeName nvarchar(128) = N'LiteBaselineV1HistoryBridge';

IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
BEGIN
    THROW 51000, '__EFMigrationsHistory table was not found. Run EF migrations before applying this bridge.', 1;
END;

IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @BaselineMigrationId)
BEGIN
    PRINT 'Baseline migration is already registered. No action required.';
    RETURN;
END;

-- Preconditions: only bridge databases that already match the expected schema generation.
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    THROW 51001, 'Precondition failed: dbo.Users table not found.', 1;
END;

IF OBJECT_ID(N'dbo.Models', N'U') IS NULL
BEGIN
    THROW 51002, 'Precondition failed: dbo.Models table not found.', 1;
END;

IF OBJECT_ID(N'dbo.Tools', N'U') IS NULL
BEGIN
    THROW 51003, 'Precondition failed: dbo.Tools table not found.', 1;
END;

IF OBJECT_ID(N'dbo.UsageEvents', N'U') IS NULL
BEGIN
    THROW 51004, 'Precondition failed: dbo.UsageEvents table not found.', 1;
END;

IF COL_LENGTH(N'dbo.UsageEvents', N'ChargeUsd') IS NULL
BEGIN
    THROW 51005, 'Precondition failed: dbo.UsageEvents.ChargeUsd column not found.', 1;
END;

IF COL_LENGTH(N'dbo.UsageEvents', N'MarkupPercent') IS NULL
BEGIN
    THROW 51006, 'Precondition failed: dbo.UsageEvents.MarkupPercent column not found.', 1;
END;

IF COL_LENGTH(N'dbo.UsageEvents', N'InvokingAssistantId') IS NULL
BEGIN
    THROW 51007, 'Precondition failed: dbo.UsageEvents.InvokingAssistantId column not found.', 1;
END;

IF OBJECT_ID(N'dbo.DocumentChunks', N'U') IS NULL
BEGIN
    THROW 51008, 'Precondition failed: dbo.DocumentChunks table not found.', 1;
END;

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @ExpectedLegacyHead)
BEGIN
    THROW 51009, 'Precondition failed: expected legacy migration head is missing. Bring database to current pre-squash head before bridging.', 1;
END;

BEGIN TRANSACTION;

-- Serialize bridge registration and avoid duplicate insert on concurrent execution.
IF EXISTS (
    SELECT 1
    FROM dbo.__EFMigrationsHistory WITH (UPDLOCK, HOLDLOCK)
    WHERE MigrationId = @BaselineMigrationId
)
BEGIN
    COMMIT TRANSACTION;
    PRINT 'Baseline migration was registered by another session.';
    RETURN;
END;

INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
VALUES (@BaselineMigrationId, @BaselineProductVersion);

IF OBJECT_ID(N'dbo.__EFSquashHistoryBridgeAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.__EFSquashHistoryBridgeAudit
    (
        Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        BridgeName nvarchar(128) NOT NULL,
        BaselineMigrationId nvarchar(150) NOT NULL,
        LegacyHeadMigrationId nvarchar(150) NOT NULL,
        LegacyHistoryCount int NOT NULL,
        AppliedAtUtc datetime2(7) NOT NULL,
        AppliedBy sysname NULL
    );
END;

INSERT INTO dbo.__EFSquashHistoryBridgeAudit
(
    BridgeName,
    BaselineMigrationId,
    LegacyHeadMigrationId,
    LegacyHistoryCount,
    AppliedAtUtc,
    AppliedBy
)
SELECT
    @BridgeName,
    @BaselineMigrationId,
    @ExpectedLegacyHead,
    COUNT(*),
    SYSUTCDATETIME(),
    SUSER_SNAME()
FROM dbo.__EFMigrationsHistory
WHERE MigrationId <> @BaselineMigrationId;

COMMIT TRANSACTION;

SELECT
    BaselineMigrationId = @BaselineMigrationId,
    BaselineProductVersion = @BaselineProductVersion,
    LegacyHeadMigrationId = @ExpectedLegacyHead,
    LegacyHistoryCount = (
        SELECT COUNT(*)
        FROM dbo.__EFMigrationsHistory
        WHERE MigrationId <> @BaselineMigrationId
    ),
    BaselineRegistered = CASE
        WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @BaselineMigrationId) THEN 1
        ELSE 0
    END;
