# Entity Framework Core Commands Cheat Sheet

## IMPORTANT: Project Structure and Paths

### Project Layout
```
/UI/                                # You are likely in this directory
    /GuideAntsApi/                 # API project is HERE, not up directories
    /GuideAntsApi.DataModel/       # DataModel project is HERE, not up directories
    /src/                          # Frontend source
    /node_modules/                 # Frontend dependencies
    ... other frontend files
```

### Path Rules
1. NEVER try to navigate up directories with ../
2. ALWAYS use the projects where they are in the UI directory
3. Use these exact commands from the UI directory:

```powershell
# Create migration
dotnet ef migrations add MigrationName --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj

# Apply migration
dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

## Migration Commands

### Create a New Migration
```powershell
# From UI directory
dotnet ef migrations add MigrationName --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

### Remove Last Migration
```powershell
dotnet ef migrations remove --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

### List All Migrations
```powershell
dotnet ef migrations list --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

### Apply Migrations
```powershell
# Update database to latest migration
dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj

# Update to a specific migration
dotnet ef database update MigrationName --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

### Generate SQL Script
```powershell
# Generate SQL script for all migrations
dotnet ef migrations script --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj

# Generate SQL script from a specific migration to another
dotnet ef migrations script PreviousMigrationName TargetMigrationName --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

## Database Operations

### Drop Database
```powershell
dotnet ef database drop --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj

# With confirmation bypassed
dotnet ef database drop --force --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

### Create Database
```powershell
# The database is automatically created when running:
dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

## Common Options

### Specify Context
```powershell
--context ApplicationDbContext
```

### Specify Configuration
```powershell
--configuration Release
```

### Project Structure
```powershell
# Always use paths relative to the UI directory
--project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj

# If you MUST use absolute paths (not recommended), use the full path:
--project D:\path\to\UI\GuideAntsApi.DataModel\GuideAntsApi.DataModel.csproj --startup-project D:\path\to\UI\GuideAntsApi\GuideAntsApi.csproj
```

## Environment Variables

### Set Connection String
```powershell
# Windows PowerShell
$env:DB_CNN="Server=(localdb)\mssqllocaldb;Database=GuideAntsDb;Trusted_Connection=True"

# Windows CMD
set DB_CNN=Server=(localdb)\mssqllocaldb;Database=GuideAntsDb;Trusted_Connection=True

# Linux/macOS
export DB_CNN="Server=(localdb)\mssqllocaldb;Database=GuideAntsDb;Trusted_Connection=True"
```

## Package Management

### Add EF Core Packages
```powershell
# Add SQL Server provider
dotnet add package Microsoft.EntityFrameworkCore.SqlServer

# Add EF Core design tools (for migrations)
dotnet add package Microsoft.EntityFrameworkCore.Design

# Add EF Core tools (global installation)
dotnet tool install --global dotnet-ef
```

## Common Development Tasks

### Update Database After Pull
```powershell
git pull
dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

### Reset Database to Clean State
```powershell
dotnet ef database drop --force --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

### Generate Migration After Model Changes
```powershell
dotnet ef migrations add DescriptiveChangeName --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

## Troubleshooting

### Reset Migrations
```powershell
# Remove all migrations (but keep the database)
del GuideAntsApi.DataModel/Migrations/*
# Or on Linux/macOS
rm GuideAntsApi.DataModel/Migrations/*

# Remove database
dotnet ef database drop --force --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj

# Create fresh initial migration
dotnet ef migrations add InitialCreate --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

### Fix Migration History
```powershell
# If migrations and database get out of sync
dotnet ef database update 0 --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

## Lite baseline (squashed history) workflows

The repo may use a **lite baseline** migration (`20260325193437_LiteBaselineV1`) plus an **idempotent bridge** script for databases that already applied the pre-squash migration chain through `20260310231555_AddLlamaCppReasoningEffortString`.

### Fresh database bootstrap

For a new database (no `__EFMigrationsHistory` or empty history), apply migrations normally. No bridge runs in that case.

```powershell
dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj --connection "<your connection string>"
```

### Existing database bridge

If the database schema is already at the pre-squash head but `__EFMigrationsHistory` does **not** yet list the lite baseline, run the bridge **before** the next `dotnet ef database update` so EF history matches the squashed model:

- Script path: `src/server/GuideAntsApi/Database/DevScripts/bridge-lite-baseline-history.sql`
- Preconditions and behavior are defined in that script (it registers `20260325193437_LiteBaselineV1` and records an audit row when appropriate).

After a successful bridge, run:

```powershell
dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj --connection "<your connection string>"
```

### Post-bridge migration update

After the bridge, `dotnet ef database update` applies only migrations **after** the lite baseline. Use the same `--project`, `--startup-project`, and `--connection` as your environment requires.

### Repeatable validation

Use `src/server/validate-lite-baseline.ps1` for local dual-path checks (fresh DB vs. simulated legacy history) without `sqlcmd`.

## Re-squash checklist (lite-pruning pass)

Use this when replacing the migration chain with a new baseline again:
0. Create a backup of the database
1. Confirm production (or target) databases are at the agreed **legacy head** migration id before bridging.
2. Update `GuideAntsApi/Database/DevScripts/bridge-lite-baseline-history.sql`: `ExpectedLegacyHead`, baseline `MigrationId` / `ProductVersion`, and precondition checks so they match the new baseline and schema.
3. Regenerate or adjust the baseline migration and Designer snapshot in `GuideAntsApi.DataModel` so the model matches the intended schema.
4. Run `src/server/validate-lite-baseline.ps1` (or equivalent) for both paths before merging.
5. Deploy: ensure `Apply-DatabaseMigrations` (or your pipeline) still runs bridge-then-update in the correct order for existing databases.
6. Document any one-time manual steps for environments that skip automation. 