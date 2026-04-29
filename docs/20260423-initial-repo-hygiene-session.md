# 2026-04-23 Initial Repo Hygiene Session

## Goal

Prepare this codebase for an initial commit to a new repository while reducing the chance of carrying forward secrets, PII, local-only configuration, and obsolete tooling.

## What We Did

### 1. Audited the repo for risky material

We scanned the workspace for:

- plaintext credentials and API keys
- cloud service endpoints and tenant/app identifiers
- personal or business email addresses
- backup metadata and operational details
- runtime content and local-machine residue

High-signal findings from the audit included:

- committed local settings files containing live-looking secrets
- a checked-in ASP.NET Data Protection key file
- an obsolete sample database seeder containing a real-looking email address
- backup metadata and rollout docs containing local machine paths and dev credentials
- runtime content already present under `docker/volumes/content-files`

### 2. Ignored local settings files and added safe templates

Updated `.gitignore` to ignore these local-only settings files:

- `/appsettings.json`
- `/appsettings.Development.json`
- `/src/server/GuideAntsApi/appsettings.json`
- `/src/server/GuideAntsApi/appsettings.Development.json`
- `/src/server/GuideAntsApi/appsettings.txt`

Added sanitized example templates:

- [appsettings.example.json](D:/repos/GuideAnts/appsettings.example.json)
- [appsettings.Development.example.json](D:/repos/GuideAnts/appsettings.Development.example.json)
- [appsettings.example.json](D:/repos/GuideAnts/src/server/GuideAntsApi/appsettings.example.json)
- [appsettings.Development.example.json](D:/repos/GuideAnts/src/server/GuideAntsApi/appsettings.Development.example.json)
- [appsettings.example.txt](D:/repos/GuideAnts/src/server/GuideAntsApi/appsettings.example.txt)

These templates preserve expected structure while replacing secrets with placeholders and notes.

### 3. Verified `docker/volumes/content-files` ignore behavior

We confirmed that `docker/volumes/content-files` was already being ignored by its nested [.gitignore](D:/repos/GuideAnts/docker/volumes/content-files/.gitignore), which keeps only `.gitignore` and `.gitkeep`.

This corrected an earlier assumption that the directory needed new ignore rules at the repo root.

### 4. Replaced the checked-in Data Protection key

The old key file in `src/server/GuideAntsApi/.data-protection/settings-keys/` was replaced with a fresh bootstrap key:

- [key-e370885c-a57a-4386-8115-ad61552d65f7.xml](D:/repos/GuideAnts/src/server/GuideAntsApi/.data-protection/settings-keys/key-e370885c-a57a-4386-8115-ad61552d65f7.xml)

The new file includes explicit comments that:

- it is bootstrap-only for development
- it should be changed per environment
- it should not be shared across local, test, staging, and production

### 5. Verified the old sample seeder was not part of normal app startup

We traced `SampleDataSeeder` usage and confirmed:

- the main API startup path does not invoke it
- the only runtime call site was the standalone `DatabaseSeeder` console project
- a `SeedSampleData` option existed in the API code but was unused

Relevant files checked during verification:

- [Program.cs](D:/repos/GuideAnts/src/server/GuideAntsApi/Program.cs)
- [SqlServerDatabaseInitializer.cs](D:/repos/GuideAnts/src/server/GuideAntsApi/Database/SqlServerDatabaseInitializer.cs)
- `src/server/DatabaseSeeder/Program.cs`
- `src/server/GuideAntsApi.DataModel/Extensions/DatabaseExtensions.cs`

### 6. Removed the obsolete sample seeder and seeder project

Deleted:

- `src/server/GuideAntsApi.DataModel/Data/SampleDataSeeder.cs`
- `src/server/GuideAntsApi.DataModel/Extensions/DatabaseExtensions.cs`
- the standalone `DatabaseSeeder` project from [GuideAntsApi.sln](D:/repos/GuideAnts/src/server/GuideAntsApi.sln)
- the `DatabaseSeeder` project files and its appsettings/example appsettings files
- the dead `src/server/GuideAntsApi/Options/CommandLineOptions.cs`

Also removed the no-longer-needed `DatabaseSeeder` ignore entries from [`.gitignore`](D:/repos/GuideAnts/.gitignore).

> **Note (2026-04-29):** A purpose-built replacement —
> `RequiredGuidesAssistantsSeeder` — has since been added to the main API
> startup path. It imports required guides and assistants from
> folder-based seeds in `Resources/bootstrap/` using the existing
> guide/assistant export/import format. Unlike the removed
> `SampleDataSeeder`, it is idempotent (skips entities that already exist)
> and does not contain sample user data. See
> [setup-guide.md § Required guides and assistants](setup-guide.md#required-guides-and-assistants-bootstrap-seeding).

## Verification Performed

Successful project builds:

- [GuideAntsApi.DataModel.csproj](D:/repos/GuideAnts/src/server/GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj)
- [GuideAntsApi.csproj](D:/repos/GuideAnts/src/server/GuideAntsApi/GuideAntsApi.csproj)

Full solution build:

- [GuideAntsApi.sln](D:/repos/GuideAnts/src/server/GuideAntsApi.sln) does not fully build yet
- current failure appears unrelated to the seeder removal
- the reported error was in `TurnManagerTests`, requiring a logger argument for `TurnManager`

## Remaining Items Worth Reviewing Before First Commit

These were identified as likely future-regret candidates but were not changed in this session:

### A. Data Protection key strategy

Even with the new bootstrap key, the safest long-term approach is still to avoid committing runtime Data Protection keys at all and let each environment generate or receive its own key material.

### B. Hard-coded dev credentials in compose/docs/code

Examples still exist in:

- [docker-compose.yml](D:/repos/GuideAnts/docker/docker-compose.yml)
- [ApplicationDbContextDesignTimeFactory.cs](D:/repos/GuideAnts/src/server/GuideAntsApi.DataModel/ApplicationDbContextDesignTimeFactory.cs)
- [provider-routing-rollout-checklist.md](D:/repos/GuideAnts/docs/provider-routing-rollout-checklist.md)

These may be acceptable as local defaults, but they are still worth deciding explicitly before the first commit.

### C. Backup metadata and local-machine residue in docs

Examples:

- [20260414-provider-routing-backup-metadata.txt](D:/repos/GuideAnts/docs/20260414-provider-routing-backup-metadata.txt)
- [provider-routing-rollout-checklist.md](D:/repos/GuideAnts/docs/provider-routing-rollout-checklist.md)
- [provider-service-routing-working-draft.md](D:/repos/GuideAnts/docs/provider-service-routing-working-draft.md)

These expose backup names, timestamps, host paths, and machine-specific workflow details.

### D. Workspace/local-machine file references

Example:

- [waterfall-major-refactor.code-workspace](D:/repos/GuideAnts/waterfall-major-refactor.code-workspace)

This still references a sibling local repo path and may not belong in a clean initial public/private repo snapshot.

## Net Result

By the end of this session, the repo is in better shape for an initial commit because:

- local appsettings files are no longer intended to be committed
- safe example settings files now exist
- the obsolete sample seeder and its project are gone
- the checked-in Data Protection key was refreshed and documented as environment-specific
- we verified some prior concerns rather than guessing

The biggest remaining judgment call is whether to keep or further sanitize the current docker/dev credential defaults and operational docs before the first commit.
