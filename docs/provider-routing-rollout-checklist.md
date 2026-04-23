# Provider Routing Rollout Checklist

Date: 2026-04-14

> Status: historical rollout checklist for the 2026-04-14 migration run.
> This is not current install guidance and should not be used to infer
> present-day default behavior.

## 1) Pre-Migration Full DB Backup
Run this before applying `20260414183022_RemoveApplicationSettingsConfigModeAndProviderRouting`.

```powershell
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$containerBackupPath = "/var/opt/mssql/data/backups/guideants-dev-$timestamp.bak"
$hostBackupPath = "D:\Elumenotion\backups\guideants-dev-$timestamp.bak"

sqlcmd -S "localhost,1434" -U "sa" -P "YourStrong!Passw0rd" -Q @"
BACKUP DATABASE [guideants-dev]
TO DISK = N'$containerBackupPath'
WITH FORMAT, INIT, CHECKSUM, STATS = 5;
"@

New-Item -ItemType Directory -Force -Path "D:\Elumenotion\backups" | Out-Null
docker cp "docker-mssql-express-1:$containerBackupPath" "$hostBackupPath"

Write-Host "Backup created: $hostBackupPath"
Get-FileHash $hostBackupPath -Algorithm SHA256
```

Notes:
- SQL Server in docker cannot write directly to arbitrary host paths.
- SQL Express does not support `WITH COMPRESSION`, so backup options must omit it.

## 2) Capture Restore Metadata

```powershell
sqlcmd -S "localhost,1434" -U "sa" -P "YourStrong!Passw0rd" -Q "RESTORE HEADERONLY FROM DISK = N'/var/opt/mssql/data/backups/guideants-dev-<timestamp>.bak';"
sqlcmd -S "localhost,1434" -U "sa" -P "YourStrong!Passw0rd" -Q "RESTORE FILELISTONLY FROM DISK = N'/var/opt/mssql/data/backups/guideants-dev-<timestamp>.bak';"
```

## 3) Apply Migration

```powershell
# Option A: apply from generated SQL script
sqlcmd -S "localhost,1434" -U "sa" -P "YourStrong!Passw0rd" -d "guideants-dev" -i "D:\Elumenotion\repos\waterfall\docs\20260414-provider-routing-migration.sql"

# Option B: EF migration update
# dotnet ef database update --project src/server/GuideAntsApi.DataModel --startup-project src/server/GuideAntsApi
```

## 4) Rollback Drill Command (Prepared)

```powershell
sqlcmd -S "localhost,1434" -U "sa" -P "YourStrong!Passw0rd" -Q @"
ALTER DATABASE [guideants-dev] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [guideants-dev]
FROM DISK = N'/var/opt/mssql/data/backups/guideants-dev-<timestamp>.bak'
WITH REPLACE, RECOVERY, STATS = 5;
ALTER DATABASE [guideants-dev] SET MULTI_USER;
"@
```

## 5) Post-Migration Verification

```sql
SELECT SectionName, COUNT(*) AS Cnt
FROM dbo.ApplicationSettings
GROUP BY SectionName
HAVING COUNT(*) > 1;

SELECT TOP 100 SectionName, JsonValue
FROM dbo.ApplicationSettings
WHERE SectionName IN ('SpeechTranscription','SpeechSynthesis','ImageGeneration','Embeddings','DocumentIntelligence');

SELECT TOP 20 *
FROM dbo.ApplicationSettingsBackup_20260414_ProviderRouting
ORDER BY BackupId DESC;
```

## 6) Execution Record (2026-04-14)
- Executed backup file:
  - host: `D:\Elumenotion\backups\guideants-dev-20260414-144255.bak`
  - container: `/var/opt/mssql/data/backups/guideants-dev-20260414-144255.bak`
- SHA256:
  - `D8F70A631B191BADB192C865BB79C133B4C2AF1C2D5CBF1D776A84D68D1FE0EC`
- Restore metadata + hash record:
  - `docs/20260414-provider-routing-backup-metadata.txt`
- WebAPI+UI runtime redeploy after migration:
  - image: `guideants-webapi-ui:26104.1451`
  - service recreate: `docker compose --profile webapi-ui up -d --no-deps --force-recreate guideants-webapi-ui`
  - smoke checks: `GET /api/settings/sections` succeeded; service sections returned expected `ActiveProviderId` values; compose `LocalServiceHosts__*` env roots present.
- GuideAnts AI runtime redeploy for ASR startup reliability:
  - image: `guideants-ai:cuda13-26104.1510`
  - service recreate: `docker compose up -d --no-deps --force-recreate guideants-ai`
  - verification: `GA_ASR_AUTO_LOAD_ON_STARTUP=1`; `/asr/health` reached `loaded=true`; `/api/speech/transcribe` returned successful transcription.
