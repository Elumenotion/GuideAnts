# GuideAnts database reference

How to read the GuideAnts SQL Server and answer questions about it. This is the
**persistent understanding** so you do not have to re-derive the schema each
session. Column lists are available live via the tool (`schema <Table>`); this
doc explains what the tables *mean* and how they *join*.

## The one idea

GuideAnts is "a structured workspace for AI work." Everything is organized as
**Projects > Notebooks**, and two kinds of activity live on a notebook:
**files** (versioned content + generated artifacts) and **conversations**
(chats with assistants/guides). Guides/Assistants package the *way* of working;
Agent Invocations and JobQueue are how that work *runs*; UsageEvent is the
telemetry/cost layer.

## SQL table names (plural!)

This document uses singular entity names; **the SQL tables are plural**:
`Projects`, `Notebooks`, `NotebookFiles`, `NotebookConversations`,
`NotebookConversationMessages`, `ConversationTurns`, `MessageAttachments`,
`AgentInvocations`, `AgentInvocationMessages`, `UsageEvents`,
`FileLineageEvents`, `Assistants`, `Models`, `ContentFiles`,
`ContentFileVersions`, `AssistantTools`, etc. A few stay singular:
`JobQueue`. If a query fails with *"Invalid object name"*, pluralize the
table first.

Column gotchas verified against the live schema (they will not match the
entity intuition):
- `Notebooks` has **`Title`** (not `Name`), plus `Slug`, `Description`.
- `NotebookConversations`: `Title`, `Summary`, `NotebookId`, `Created`
  (there is **no** `Updated`).
- `MessageAttachments` uses **`Type`** (not `AttachmentType`); it also
  carries a denormalized `RelativePath`, `OrderIndex`, and nullable
  `UploadType` — often no join to `NotebookFiles` is needed.
- `NotebookFiles` uses **`LastModifiedUtc`** (not `Updated`);
  `DocumentId` is NOT NULL (empty string when nothing was extracted).
- `NotebookConversationMessages.Role` and `.MessageContentType` are
  **`int`** in this deployment (same enum values as the tinyint columns).
- `ConversationTurns.Status` and `AgentInvocations.Status` are
  **strings**: observed `completed`, `cancelled`, `running`.

## Feature areas and their tables

### 1. Workspace (projects, notebooks, files)
The backbone. Start most questions here.
- **Project** — top-level container. `Id`, `HomePageContentFileId`.
- **ProjectFolder** — folders inside a project (`ParentFolderId` for nesting).
- **Notebook** — the working surface. `ProjectId`, `Slug`, `GuideId` (which guide
  created it), `SourceNotebookId`/`SourceConversationMessageId` (cloning provenance),
  `HomePageFileId` / `HomePageConversationId` (the landing view).
- **NotebookFile** — a file living in a notebook. `RelativePath`, `FileHash`,
  `OriginContentFileVersionId` (traces a notebook file back to the project content
  version it came from), `DocumentId` (link to extracted/embedded text).
- **ContentFile** / **ContentFileVersion** — the project-level, versioned document
  store. `ContentFile` = the logical file (`Path`, `RelativePath`, `LatestVersion`);
  `ContentFileVersion` = each revision (`OriginNotebookId`/`OriginNotebookFileId`
  track a version that originated from a notebook file). **Versioned content, not
  raw bytes, is the unit of lineage.**
- **FileLineageEvent** — an append-only log of what happened to a file:
  `Action` (enum `FileLineageAction`), `FileKind` (Project/Notebook), `FileId`,
  `VersionNumber`, `ProjectId`, `NotebookId`, `StoragePath`. **This is the answer
  to "where did this file come from / what did it become."**

### 2. Conversations (the chats)
Two parallel families — do not confuse them:
- **Guide-level chat**: `ConversationTurn` (+ `ConversationTurnTrace`,
  `ConversationLock`, `ConversationCurrentState`). These are turns for a
  guide's own home-page conversation; keyed by `NotebookConversationId` (a turn
  is attached to a notebook conversation).
- **Notebook chat**: `NotebookConversation` -> `NotebookConversationMessage`
  (the actual messages, `Role` = `ChatRole`, `Content`, `ToolCalls`,
  `ThinkingBlocksJson`, `MessageContentType`). A conversation also has
  `Turns` (`ConversationTurn`) which group the messages into user/assistant turns
  (`TurnIndex`, `MessageSequence`).

  **Message row anatomy (verified):** user rows have `AssistantName =
  'user'` and the instruction in `Content`; assistant rows carry
  `AssistantName` + `ModelDeploymentId`, and the tool calls they issued in
  `ToolCalls` (JSON array of `{id, type, function:{name, arguments}}`);
  tool rows (`Role = 4`) have `FunctionName` set, `ToolCallId` matching the
  call, and the tool result in `Content`. An assistant row with
  empty `Content` (len 0) is a streaming placeholder, not a real answer —
  the turn's answer is the *last* non-empty `Role = 3` row for that
  `TurnIndex`.
- **MessageAttachment** — files referenced/created by a message
  (`MessageId`, nullable `NotebookFileId`, `Type` = AttachmentType
  enum, denormalized `RelativePath`). Note: conversations whose agent
  writes files straight into the notebook `Output/` directory can have
  **zero** attachment rows — the files are then visible as
  `NotebookFiles` rows and in `ConversationTurn.FilesCreated` /
  `.FilesModified`.
- **MessageEditHistory** — user edits to a message.

**To answer "what did a conversation produce":** `NotebookConversation` ->
`NotebookConversationMessage` (`ToolCalls` / `MessageAttachment.NotebookFileId`)
and -> `ConversationTurn` (`FilesCreated` / `FilesModified` columns) ->
`NotebookFile`. The `MessageAttachment` rows are the cleanest file->message link.

### 3. Guides & Assistants (packaging)
- **Assistant** — a single reusable agent. `Kind` = `AssistantKind`
  (`Assistant=0`, `Guide=1` — a Guide is just a kind of Assistant). `ModelId`,
  `Instructions`, `Tools`, `Files`, `ContextOptions`, `SkillMetas`,
  `ConversationStarters`, `SandboxWireApiConfigJson`.
- **AssistantTool** / **Tool** — which tools an assistant exposes
  (`AssistantTool.AssistantId` + `.ToolId`).
- **AssistantFile** / **AssistantFileMarkdownShadow** — files packaged in an
  assistant.
- **AssistantContextOption** / **AssistantConversationStarter** — context menus
  and suggested first messages.
- **AssistantSkillMeta** — skills attached to the assistant.
- **AssistantOpenApiSchema** / **AssistantOpenApiOperation** — the assistant's
  exposed API surface (for embedding).
- **GuideMember** — membership: which assistants form a guide's "crew"
  (`GuideId` = the Guide assistant, `AssistantId` = a member).
- **PublishedGuide** — a shared/embedded instance. `GuideId`, `NotebookId`,
  `AuthMode` = `PublishedGuideAuthMode`, `ApiKeyHash`, billing limits.

### 4. Execution (agent runs + background jobs)
- **AgentInvocation** — one run of an assistant (possibly nested via
  `ParentInvocationId`). `ParentConversationId` + `ParentTurnIndex` say
  *where in the chat* it was spawned; `TriggeringToolCallId` is the id of
  the tool call in the parent chat message that launched it;
  `Instructions` is the task brief; `Status` (string:
  `completed`/`cancelled`/`running`), `ErrorMessage`, `LlmRoundTrips`,
  `ToolCallCount`, `Depth`, `DurationMs`, `Completed`. `UsageJson` holds
  per-run token totals (`prompt_tokens`, `completion_tokens`,
  `total_tokens`, `CachedPromptTokens`).
- **AgentInvocationMessage** — the run's transcript: `Sequence` (0 = the
  user task brief), `Role` (int, same `ChatRole` values), `Content`,
  `ToolCallsJson` (on assistant rows: the calls issued in that step, with
  arguments), `FunctionName` + `ToolCallId` on tool rows with the tool
  result in `Content`. A parallel fan-out appears as several depth-1
  children of one depth-0 invocation, created within the same second;
  `ParentInvocationId` is NULL at the top level. Common workers: `Search`,
  `Read Web`, `Code Executor`, `Conversation Title Generator` (auto, one
  round trip, no tools), `Diagrams`, `Media Creator`.
- **JobQueue** — durable background work. `JobType` (string, e.g.
  `SyncNotebook`, `ExtractNotebookFileMarkdown`), `Status` = `JobStatus` enum,
  `PayloadJson` (the input), `Attempts`/`MaxAttempts`, `LeaseUntil`
  (claim lock), `ClaimToken`, `ErrorMessage`. **Diagnosing "why is X stuck"
  starts here** (status 3=Completed, 4=Failed, 0=Pending).

### 5. Telemetry & cost
- **UsageEvent** — one billable/telemetry event. `Category` = `UsageCategory`
  enum, `Service` (e.g. AzureOpenAI), `Operation`, `ModelDeploymentId`,
  token counters (`ValueInput`/`ValueOutput`/`ValueCachedInput`/`ValueReasoning`/
  `ValueOther`), `CostUsd`, `MarkupPercent`, `ChargeUsd`. Richly keyed to
  `UserId`, `ProjectId`, `NotebookId`, `AssistantId`, `AgentInvocationId`, etc.
  **Cost/usage questions aggregate this table** (usually `GROUP BY Category` or
  `Service`, filtered by `Created` range / `ProjectId`).
- **UsageReportCategory** / **UsageReportCategoryOperation** — taxonomy for
  reporting.

### 6. Local AI backends
- **Model** — a model definition (`ModelId` string is the deployment id used
  everywhere as `ModelDeploymentId`).
- **LocalModelInstallation** — a locally installed model (`ModelId`, `CatalogId`,
  `QuantId`).
- **LocalModelOperation** — install/update operations.

### 7. Host integration
- **HostFolderMount** / **HostFolderMountLink** — a host folder mounted into a
  project/notebook (`ProjectId`, `NotebookId`, `Status` = `HostFolderMountStatus`);
  links map the mount to specific notebooks.
- **ProjectScheduledJob** / **ProjectScheduledJobRun** — cron/manual jobs
  (`Type` = NewConversation/RunPythonScript, `Trigger`, runs have `Status`).
- **ExcludedHost** — hosts excluded from discovery.

### 8. Auth & identity
- **User** / **UserRole** (`Role` enum), **CliAuthSession**
  (`Status` = `CliAuthSessionStatus`), **ExternalOAuthToken**,
  **OAuthAuthorizationState**, **AssistantAuthProvider** /
  **AssistantAuthScope**, **ProjectExternalAuth**.

## Reading a conversation (URL → story)

A conversation URL encodes the keys directly:
`http://localhost:5107/projects/{ProjectId}/notebooks/{NotebookId}?c={ConversationId}`.

Recipe (all four verified):

1. **Resolve the header** (project/notebook/conversation titles, when):

   ```sql
   SELECT p.Title AS ProjectTitle, nb.Title AS NotebookTitle, nb.Slug,
          c.Title AS ConvTitle, c.Created
   FROM NotebookConversations c
   JOIN Notebooks nb ON nb.Id = c.NotebookId
   JOIN Projects p   ON p.Id  = nb.ProjectId
   WHERE c.Id = @conversationId;
   ```

2. **Message map** — the shape of the chat (roles, tools, previews, timing):

   ```sql
   SELECT TurnIndex, MessageSequence, Role, AssistantName, ModelDeploymentId,
          FunctionName, LEN(Content) AS ContentLen, LEFT(Content, 220) AS Preview, Created
   FROM NotebookConversationMessages
   WHERE NotebookConversationId = @conversationId
   ORDER BY TurnIndex, MessageSequence;   -- Role: 1=System 2=User 3=Assistant 4=Tool
   ```

3. **Turn outcomes** (who, which model, status, wall time):

   ```sql
   SELECT TurnIndex, AssistantName, ModelDeploymentId, Status, Created, LastUpdated
   FROM ConversationTurns
   WHERE NotebookConversationId = @conversationId
   ORDER BY TurnIndex;
   ```

4. **The final answer of each turn** (skip empty streaming placeholders):

   ```sql
   SELECT m.TurnIndex, m.Content
   FROM NotebookConversationMessages m
   WHERE m.NotebookConversationId = @conversationId AND m.Role = 3
     AND m.MessageSequence = (SELECT MAX(MessageSequence)
                              FROM NotebookConversationMessages
                              WHERE NotebookConversationId = m.NotebookConversationId
                                AND TurnIndex = m.TurnIndex AND Role = 3);
   ```

Interpretation notes:
- **What did it produce?** Check (a) `MessageAttachments` for the
  conversation's messages, (b) `ConversationTurns.FilesCreated` /
  `.FilesModified`, and (c) `NotebookFiles` where `NotebookId = ...` —
  agent artifacts land in the notebook's `Output/` paths. Exclude the
  packaged `Resources/Skills/...` script rows (they are skill scaffolding,
  not outputs).
- **Where did the work actually run?** `AgentInvocations`
  (`ParentConversationId` = the conversation, `ParentTurnIndex` = the turn)
  → detail in `AgentInvocationMessages`. One chat turn can fan out into a
  whole agent tree; the chat's own message table only shows the tool-call
  boundary.
- **What did it cost?** `UsageEvents` keyed by `AgentInvocationId` (or by
  `NotebookId`/`ProjectId` for the wider scope), grouped by `Category`.

## How to join (the paths you will actually use)

- **Notebook -> its files:** `Notebook.Id = NotebookFile.NotebookId`.
- **Notebook -> its conversations:** `Notebook.Id = NotebookConversation.NotebookId`.
- **Conversation -> messages -> files:** `NotebookConversationMessage.NotebookConversationId`
  and `MessageAttachment.MessageId = NotebookConversationMessage.Id` ->
  `MessageAttachment.NotebookFileId = NotebookFile.Id`.
- **Conversation -> turns:** `ConversationTurn.NotebookConversationId`;
  messages carry `TurnIndex` + `MessageSequence` to order them.
- **Assistant -> tools/files/skills:** `AssistantTool.AssistantId`,
  `AssistantFile.AssistantId`, `AssistantSkillMeta.AssistantId`.
- **Assistant -> runs:** `AgentInvocation.AssistantId`; nest via
  `AgentInvocation.ParentInvocationId`.
- **File lineage (any direction):** query `FileLineageEvent` by `FileId`
  (and `FileKind`), ordered by `Timestamp` — or by `ProjectId`/`NotebookId`
  to see all events in scope.
- **Usage for a thing:** `UsageEvent` has a nullable FK to nearly every other
  table (`ProjectId`, `NotebookId`, `AssistantId`, `AgentInvocationId`,
  `ContentFileId`, `NotebookFileId`, `PublishedGuideId`) — filter on whichever
  scope you have.
- **ModelDeploymentId** (string) on conversations/invocations/usage ->
  `Model.ModelId`.

## Enum dictionary (tinyint/int status columns)

(Role / MessageContentType / MessageAttachment.Type are `int` in this
deployment; the values are the same.)

| Column(s) | Enum | Values |
|-----------|------|--------|
| `JobQueue.Status` | `JobStatus` | 0=Pending, 2=Processing, 3=Completed, 4=Failed, 5=Abandoned, 6=Cancelled |
| `UsageEvent.Category` | `UsageCategory` | 0=ChatCompletion, 1=ImageGeneration, 2=DocumentExtraction, 3=SpeechTranscription, 4=SpeechSynthesis, 5=Search, 6=StorageUploaded, 7=StorageSystemGenerated, 8=ToolCall, 9=Embeddings |
| `NotebookConversationMessage.Role` | `ChatRole` | 1=System, 2=User, 3=Assistant, 4=Tool |
| `NotebookConversationMessage.MessageContentType` | `MessageContentType` | 0=Text, 1=FileReference, 2=Mixed |
| `MessageAttachment.AttachmentType` | `AttachmentType` | 0=Referenced, 1=Created, 2=Modified |
| `FileLineageEvent.Action` | `FileLineageAction` | 1=Uploaded, 2=Versioned, 3=CopiedToNotebook, 4=PublishedToProject, 5=Moved, 6=Renamed, 7=Deleted, 8=ExternalWrite, 9=Created |
| `FileLineageEvent.FileKind` | `FileKind` | 0=Project, 1=Notebook |
| `Assistant.Kind` | `AssistantKind` | 0=Assistant, 1=Guide |
| `UserRole.Role` | `Role` | 0=Pending, 1=Reader, 2=Contributor, 3=Admin |
| `CliAuthSession.Status` | `CliAuthSessionStatus` | 0=Pending, 1=Approved, 2=Consumed, 3=Denied |
| `PublishedGuide.AuthMode` | `PublishedGuideAuthMode` | 0=Anonymous, 1=Webhook, 2=ApiKey, 3=AppIdentity |
| `HostFolderMount.Status` | `HostFolderMountStatus` | 0=PendingRestart, 1=Active, 2=PendingRemoval, 3=Removed, 4=Error |
| `HostFolderMountLink.Status` | `HostFolderMountLinkStatus` | 0=PendingRestart, 1=PendingLink, 2=Linked, 3=Unlinked, 4=LinkError, 5=UnlinkError |
| `HostFolderMount.Scope` | `HostFolderMountScope` | 0=Notebook, 1=Project |
| `ProjectScheduledJob.Type` | `ProjectScheduledJobType` | 0=NewConversation, 1=RunPythonScript |
| `ProjectScheduledJob.Trigger` | `ProjectScheduledJobTrigger` | 0=Schedule, 1=Manual |
| `ProjectScheduledJobRun.Status` | `ProjectScheduledJobRunStatus` | 0=Running, 1=Succeeded, 2=Failed, 3=Cancelled |
| `ProjectScheduledJob.LastRunStatus` | `ProjectScheduledJobLastRunStatus` | 0=Succeeded, 1=Failed |
| `ContentFile`/`ContentFileVersion` upload type | `ContentUploadType` | 0=ImageFile, 1=ImageUrl, 2=AudioFile, 3=TextFile, 4=SandboxFile, 5=Folder |
| markdown extraction (shadows) | `MarkdownExtractionStatus` | 0=Pending, 1=Processing, 2=Completed, 3=Failed, 4=Skipped |
| host source kind | `SourceKind` | 0=LocalPath, 1=Smb |

**Watch out:** `AgentInvocation.Status` and `ConversationTurn.Status` are
**strings**, not enums (e.g. `running`, `completed`) — check distinct values if
you are unsure. Same for `ConversationTurn.Traces.CaptureState` and the
segment `status`/`terminalStatus` inside `TraceJson` (all free strings).

**`ConversationTurn.Status` (string) — observed:** `completed`, `cancelled`,
`failed`, `interrupted`, `pending_client_tool`, `streaming`.

**`ConversationTurn.TerminationCode` (string, nullable) — observed:**
`cancelled` ("Stream was cancelled by user"), `cancel_requested`,
`stream_interrupted` (heartbeat stopped, turn recovered), `local_llm_crashed`
(runtime crashed mid-stream), `stream_setup_failed`, or NULL with a raw error
in `TerminationDetail` (e.g. a provider auth error). `TerminalizedAt` is set
when the turn reaches a terminal state.

**`ConversationTurnTraces.CaptureState` (string):** `completed`, `cancelled`,
`failed`, `partial` — the capture's own health, independent of the turn.

**Trace segment `status` / `terminalStatus` (string):** segment `status` is
`partial`/`completed`/`cancelled`; `terminalStatus` is `stop` / `tool_calls` /
`pending_client_tool` / `cancelled`.

## Column semantics worth knowing

- **`UsageEvents.UserId` is `nvarchar(900)`, not a GUID FK** — it stores an
  external identity string (or is null); do not join it to `Users.Id`.
- **IDs are `uniqueidentifier`** (GUID). `RowVersion` (timestamp) on many
  tables renders as **hex** in the tool output — it is an opaque concurrency
  token, not data to display.
- **JSON payload columns** (`PayloadJson`, `UsageJson`, `ToolCalls`,
  `ThinkingBlocksJson`, `SamplingParametersJson`, `MetadataJson`,
  `*ConfigJson`) hold unstructured payloads — filter on the typed columns,
  parse the JSON in the client (they are not queryable with SQL `WHERE` on inner
  fields without JSON functions).
- **`DocumentId`** (string) on `NotebookFile`/`ContentFile`/`DocumentChunk` links
  a file to its extracted/embedded representation.
- **`DocumentChunk`** is the RAG/vector index: one row per chunk, `Content`
  (the text), `Embedding` (float vector), `ChunkIndex`, plus a nullable pointer
  to exactly one of `ContentFileId`/`NotebookFileId`/`AssistantFileId`.
- **`ModelDeploymentId`** (string) is the join key to `Model.ModelId`.
- **Timestamps:** `Created`/`Updated`/`LastModifiedUtc` are UTC. Order
  conversations with `TurnIndex` then `MessageSequence`.

## Worked question patterns

- **"What's the status of the background jobs?"**
  `SELECT JobType, Status, COUNT(*) FROM JobQueue GROUP BY JobType, Status` —
  decode `Status` via `JobStatus`.
- **"How much did project X cost in the last 30 days?"**
  `SELECT SUM(CostUsd) FROM UsageEvents WHERE ProjectId = @p AND Created > DATEADD(day,-30,GETUTCDATE())`.
- **"Which files did notebook Y's conversations create?"**
  join `MessageAttachment` -> `NotebookFile` where the message's
  `NotebookConversation.NotebookId = @notebook` and `AttachmentType = 1`
  (Created).
- **"Trace a file's history"** — `SELECT * FROM FileLineageEvents WHERE FileId =
  @f ORDER BY Timestamp`.
- **"What guides exist and how many notebooks did they create?"**
  `SELECT a.Name, COUNT(n.Id) FROM Assistants a LEFT JOIN Notebooks n ON n.GuideId = a.Id WHERE a.Kind = 1 GROUP BY a.Name`.

> This document is derived from the EF entity model and the running stack.
> Verify column-level details with `schema <Table>` before writing complex
> queries; enum/relationship facts here are stable unless the app model changes.

## Usage, performance, and undone turns (verified)

### How usage is recorded — `UsageEvents`

One row per billable/telemetry event. `Category` (int = `UsageCategory`
enum) + `Service` + `Operation` describe *what*; the nullable FKs describe
*where it counts*. Observed keying density (of all rows):

| Category | rows | always keyed | also keyed by |
|---|---|---|---|
| 8 ToolCall | 16,933 | `ProjectId`+`ConversationId`+`AssistantId` | `NotebookConversationMessageId` (58%), `AgentInvocationId` (42%) |
| 0 ChatCompletion | 5,095 | `ProjectId`+`ConversationId`+`AssistantId` | msg (59%), invocation (41%) |
| 7 StorageSystemGenerated | 5,041 | `ProjectId` | (none — infra, no conversation) |
| 5 Search | 1,004 | `ProjectId`+`ConversationId`+`AssistantId` | invocation (82%) |
| 1 ImageGeneration | 493 | `ProjectId`+`AssistantId` | msg (33%), invocation (59%) |
| 6 StorageUploaded | 295 | `ProjectId` | (none) |
| 4 SpeechSynthesis / 3 SpeechTranscription / 9 Embeddings | small | `ProjectId` | — |

So: **chat/tool/search events carry conversation + message/invocation keys;
storage events are project-only.** `AssistantId` (the worker) is set on every
assistant-driven event; `InvokingAssistantId` is unused in this deployment.
`ExternalRequestId`/`SourceChannel` are set only for external/embedded calls.
`MetadataJson` holds the call detail (`toolCallId`, `arguments`, etc.).

**Token counters:** `ValueInput`, `ValueCachedInput`, `ValueReasoning`,
`ValueOutput`, `ValueOther` (all bigint). On ChatCompletion rows
`ValueInput` ≈ prompt tokens and `ValueCachedInput` ⊆ `ValueInput` (the
prefix that hit the prompt cache).

**Cost model (verified identity):** `CostUsd` = list cost of the event;
`MarkupPercent` is a **multiplier, not a percent** (constant `1.20` in this
deployment = 1.2×, i.e. +20%); and **`ChargeUsd = CostUsd * MarkupPercent`**
holds exactly (max |Δ| = 0.0001, rounding). Report `ChargeUsd` for "what did
the customer pay"; `CostUsd` for "what it cost us."

### How to read performance

- **The unit of performance is `AgentInvocation`** — it has `DurationMs`,
  `LlmRoundTrips`, `ToolCallCount`, `Depth`, `Status`, `ModelDeploymentId`.
  Model speed ≈ `DurationMs / LlmRoundTrips` (ms per LLM round trip).
- **Per-model throughput/cache** comes from ChatCompletion `UsageEvents`:
  tokens in/out and **cache hit rate = `SUM(ValueCachedInput) /
  SUM(ValueInput)`** per `ModelDeploymentId`. (Local Qwen models here ran at
  70–83% cache hit; `gpt-5.5` at 0%.)
- **Per-turn wall time and LLM round-trips** are in
  `ConversationTurnTraces.TraceJson` (see below) — each `rounds[]` entry has
  `createdUtc`, `modelDeploymentId`, `requestMessages`,
  `responseFinishReason`, `responseMessage`. This is the richest timing
  source and lets you see exactly which model served each round.
- **Job health** is `JobQueue` (status 3=Completed, 4=Failed) for background
  work; `AgentInvocation`/`ConversationTurn` for interactive work.

### How undone/cancelled turns are recorded

An undone turn shows up in **four** places; use them together:

1. **`ConversationTurns.Status = 'cancelled'`** (+ `failed` / `interrupted`)
   with `TerminalizedAt`, `TerminationCode`, `TerminationDetail`. This is the
   authoritative "the turn didn't finish" flag.
2. **`ConversationTurnTraces.CaptureState = 'cancelled'`** (or `failed` /
   `partial`) — the trace-capture's own view of the same turn.
3. **`ConversationTurnTraces.TraceJson.segments[]`** — a turn is made of
   *segments* (continuous stream attempts). Each segment has
   `startedUtc`/`completedUtc`, `status` (`partial`/`completed`/`cancelled`),
   `terminalStatus` (`stop`/`tool_calls`/`pending_client_tool`/`cancelled`),
   `errorMessage`, `rounds[]`, `messageEvents[]`. **Segment count > 1 means
   the turn had multiple stream attempts** (a client-tool round trip ends a
   segment at `pending_client_tool` and the next resumes; a restart or
   re-send adds a segment; a cancelled turn's last segment has
   `status = 'cancelled'`). This is the "how many times did it try" signal.
4. **`MessageEditHistories`** — *user edits*, not undos: when a user edits a
   message, the original `OriginalContent`/`OriginalToolCalls` are preserved
   here with `FirstEditedByUserId`/`FirstEditedAt`, and the live message row
   gets `IsEdited = 1`.

**Do undone turns cost money?** Yes — *partially*. A cancelled turn still
billed every external event recorded before the user hit stop (LLM round
trips that completed, tool calls that ran). In this deployment the cancelled
turns carried **$22.75** of `ChargeUsd` (75 turns), `interrupted` turns
$11.94 (3), `failed` turns $0.71 (2). A turn cancelled before any external
call (e.g. only local sandbox work) records **no** usage. So "wasted spend"
= usage joined to turns whose `Status IN ('cancelled','failed','interrupted')`
(via `NotebookConversationMessageId` → message's `TurnIndex`).

## Guides, projects, and environments (verified)

### How a guide is defined

A guide is just `Assistants` with `Kind = 1` (a plain assistant is `Kind = 0`;
`IsGlobal`/`IsActive` flags, `DisplayOrder`). The guide's definition lives on
that one row: `Instructions` (system prompt), `ModelId` (nullable — falls
back to the default model), `Temperature`/`TopP`/`ReasoningEffort` /
`SamplingParametersJson`, `ToolResourcesJson`, `HomePageMarkdown`,
`MaxToolCallsPerTurn`, `AvatarImageBytes`, `SandboxWireApiConfigJson`,
`EnvironmentConfigJson` (unused in this deployment — see below).

Its packaging sits in the satellite tables, all keyed by `AssistantId`:
- **`AssistantTools` → `Tools`** — the tools it exposes.
- **`AssistantFiles` (+ `AssistantFileMarkdownShadows`)** — packaged files.
- **`AssistantSkillMetas`** — packaged skills (`SkillName`, `Enabled`,
  `ContentHash`, `DisplayOrder`).
- **`AssistantContextOptions`** / **`AssistantConversationStarters`** —
  context menus / suggested first messages.
- **`AssistantOpenApiSchemas`/`Operations`** — embedded API surface.
- **`AssistantAuthProviders`/`Scopes`** — auth config.

**The crew** = `GuideMembers` (`GuideId` → `AssistantId`, `DisplayOrder`,
nullable `MaxToolCallsPerInvocation`). Crew members are `Kind = 0`
assistants — the workers the guide can dispatch as `AgentInvocations`
(see the Copywriting notebook: Creative Guide's crew is Search, Media
Creator, Diagrams, Code Executor, Wire Target).

### How guides relate to projects

**There is no direct Project ↔ Guide link.** The relationship is mediated by
notebooks: `Notebooks.GuideId` says *which guide created/owns this
notebook*. So "the guides of project P" = the distinct `GuideId`s across
P's notebooks, and "the projects of guide G" = distinct
`Notebook.ProjectId` over G's notebooks. `Notebook.GuideId` is also what
`UsageEvents.AssistantId` and `AgentInvocation.AssistantId` track back to.

**`PublishedGuides`** is the other project-facing link: a *shared/embedded*
instance of a guide bound to a specific notebook (`GuideId` + `NotebookId`),
with `AuthMode` (0=Anonymous, 1=Webhook, 2=ApiKey, 3=AppIdentity),
`ApiKeyHash` (never the key), display flags, `McpEnabled`/`McpDescription`,
`MaxTurns`, and billing limits (`DailyChargeLimitUsd`,
`BillingPeriodChargeLimitUsd`). Embedded-guide usage comes back with
`UsageEvents.PublishedGuideId` + `SourceChannel`/`ExternalRequestId` set.

### How environments work

Environment variables for an assistant are a **per (project × assistant)
override table**: `ProjectAssistantEnvironments`
(PK `ProjectId` + `AssistantId`, `EnvironmentConfigJson`, `Created`/`Updated`).
The JSON shape is:

```json
{ "variables": [ { "name": "...", "value": "...", "isSecret": true|false } ] }
```

Facts verified in this deployment:
- **Guide-level defaults do not exist in practice** —
  `Assistants.EnvironmentConfigJson` is NULL on every row; likewise
  `Projects.EnvironmentConfigJson` is unused. All configured environments
  live in `ProjectAssistantEnvironments` (6 rows here, across 4 projects).
  Treat the guide/project columns as future/default slots; the project row
  is the source of truth when present.
- **Secrets are envelope-encrypted**: `isSecret = true` values are stored as
  `encv2::<scope>::<ciphertext>` (e.g. `encv2::local-dev::…`) and are
  decrypted server-side at injection time. **Audit gotcha:** a variable can
  be `isSecret = false` while still holding a live token (found one:
  `TALKING_HEAD_SKILL_TOKEN` stored as a 64-hex char string, unencrypted,
  in 2 projects) — flag any `*TOKEN*`/`*PASSWORD*`/`*KEY*`/
  `CONNECTION_STRING` whose value does not start with `encv2::`.
- Typical contents: skill gateway endpoints + tokens
  (`QWEN_IMAGE_SKILL_BASE_URL`/`_TOKEN`, `AUDIOCPP_*`, `TALKING_HEAD_*`),
  host access (`GA_HOST_SSH_*`, `GA_DB_*` — the latter is how *this* skill
  gets its connection string), search (`SEARXNG_URL`).
- Because one row can exist per assistant in the same project (guide rows
  and crew rows are separate), a guide's *effective* environment for a
  project is the union of its own row and — check app behavior before
  assuming — crew members' rows.

## Core query library (verified against the live DB)

All of these ran successfully; adapt the parameters.

```sql
-- C1. Conversations in a notebook, with size
SELECT c.Id, c.Title, c.Created,
  (SELECT COUNT(*) FROM NotebookConversationMessages m WHERE m.NotebookConversationId = c.Id) AS Msgs,
  (SELECT COUNT(*) FROM ConversationTurns t WHERE t.NotebookConversationId = c.Id) AS Turns
FROM NotebookConversations c
WHERE c.NotebookId = @notebookId
ORDER BY c.Created;

-- C2. Agent invocations for a notebook (nesting + detail volume)
SELECT i.Id, i.ParentConversationId, i.ParentTurnIndex, i.ParentInvocationId,
       i.AssistantName, i.ModelDeploymentId, i.Status, i.Depth,
       i.LlmRoundTrips, i.ToolCallCount, i.DurationMs, i.Created, i.Completed,
       (SELECT COUNT(*) FROM AgentInvocationMessages m WHERE m.AgentInvocationId = i.Id) AS MsgCount,
       LEFT(i.Instructions, 140) AS Instr
FROM AgentInvocations i
WHERE i.ParentConversationId IN (SELECT Id FROM NotebookConversations WHERE NotebookId = @notebookId)
ORDER BY i.Created;

-- C3. Worker mix for a notebook (who did what, on which model)
SELECT i.AssistantName, i.ModelDeploymentId, i.Status, i.Depth, COUNT(*) AS n,
       SUM(i.LlmRoundTrips) AS RoundTrips, SUM(i.ToolCallCount) AS ToolCalls,
       SUM(i.DurationMs) AS TotalMs
FROM AgentInvocations i
WHERE i.ParentConversationId IN (SELECT Id FROM NotebookConversations WHERE NotebookId = @notebookId)
GROUP BY i.AssistantName, i.ModelDeploymentId, i.Status, i.Depth
ORDER BY n DESC;

-- C4. Invocation detail transcript (the run log)
SELECT m.Sequence, m.Role, m.FunctionName, m.ToolCallId,
       LEFT(m.Content, 200) AS Preview, m.ToolCallsJson
FROM AgentInvocationMessages m
WHERE m.AgentInvocationId = @invocationId
ORDER BY m.Sequence;

-- C5. Cost/usage for a notebook's agent runs, by category
SELECT u.Category, COUNT(*) AS n, SUM(u.CostUsd) AS Cost,
       SUM(u.ValueInput) AS InTok, SUM(u.ValueOutput) AS OutTok
FROM UsageEvents u
WHERE u.AgentInvocationId IN (
  SELECT i.Id FROM AgentInvocations i
  WHERE i.ParentConversationId IN (SELECT Id FROM NotebookConversations WHERE NotebookId = @notebookId))
GROUP BY u.Category ORDER BY n DESC;
-- Category: 0=ChatCompletion 1=ImageGeneration 4=SpeechSynthesis 5=Search 7=StorageSystemGenerated 8=ToolCall
```

```sql
-- C6. Model performance: duration + ms/round trip + failure rate
SELECT i.ModelDeploymentId, COUNT(*) AS n,
       AVG(i.DurationMs) AS AvgMs,
       AVG(i.DurationMs / NULLIF(i.LlmRoundTrips,0)) AS MsPerRoundTrip,
       SUM(i.LlmRoundTrips) AS RoundTrips,
       SUM(CASE WHEN i.Status <> 'completed' THEN 1 ELSE 0 END) AS NotCompleted
FROM AgentInvocations i
GROUP BY i.ModelDeploymentId
ORDER BY n DESC;

-- C7. Per-model throughput + prompt-cache hit rate (chat completions)
SELECT u.ModelDeploymentId,
       SUM(u.ValueInput) AS InTok, SUM(u.ValueCachedInput) AS Cached,
       ROUND(100.0 * SUM(u.ValueCachedInput) / NULLIF(SUM(u.ValueInput),0), 1) AS CachePct,
       SUM(u.ValueOutput) AS OutTok,
       ROUND(SUM(u.ChargeUsd), 2) AS ChargeUsd
FROM UsageEvents u
WHERE u.Category = 0
GROUP BY u.ModelDeploymentId
ORDER BY InTok DESC;

-- C8. Cost by category over a period (what the customer paid vs. what it cost)
SELECT Category, Service, COUNT(*) AS n,
       ROUND(SUM(CostUsd), 2) AS CostUsd, ROUND(SUM(ChargeUsd), 2) AS ChargeUsd
FROM UsageEvents
WHERE Created >= @fromUtc  -- AND ProjectId = @projectId / NotebookId = @notebookId as needed
GROUP BY Category, Service
ORDER BY ChargeUsd DESC;
-- ChargeUsd = CostUsd * MarkupPercent (multiplier; 1.20 = +20%) — exact identity.

-- C9. Undone turns: every cancelled/failed/interrupted turn with why
SELECT c.Title AS Conversation, t.TurnIndex, t.Status,
       t.TerminationCode, t.TerminationDetail, t.TerminalizedAt,
       tt.CaptureState AS TraceState
FROM ConversationTurns t
JOIN NotebookConversations c ON c.Id = t.NotebookConversationId
LEFT JOIN ConversationTurnTraces tt ON tt.ConversationTurnId = t.Id
WHERE t.Status IN ('cancelled','failed','interrupted')
ORDER BY t.TerminalizedAt DESC;
-- segment count (stream attempts) = (LEN(TraceJson)-LEN(REPLACE(TraceJson,'"segmentId"','')))/LEN('"segmentId"')

-- C10. Wasted spend: charge recorded on undone turns
SELECT t.Status, COUNT(DISTINCT t.Id) AS Turns, COUNT(*) AS UsageRows,
       ROUND(SUM(u.ChargeUsd), 4) AS WastedChargeUsd
FROM UsageEvents u
JOIN NotebookConversationMessages m ON m.Id = u.NotebookConversationMessageId
JOIN ConversationTurns t ON t.NotebookConversationId = m.NotebookConversationId
                         AND t.TurnIndex = m.TurnIndex
WHERE t.Status IN ('cancelled','failed','interrupted')
GROUP BY t.Status
ORDER BY WastedChargeUsd DESC;
-- G1. All guides with their crew size, notebooks, model
SELECT a.Id, a.Name, a.IsGlobal, a.IsActive, a.ModelId,
       (SELECT COUNT(*) FROM GuideMembers gm WHERE gm.GuideId = a.Id) AS Crew,
       (SELECT COUNT(DISTINCT n.ProjectId) FROM Notebooks n WHERE n.GuideId = a.Id) AS Projects,
       (SELECT COUNT(*) FROM Notebooks n WHERE n.GuideId = a.Id) AS Notebooks
FROM Assistants a
WHERE a.Kind = 1
ORDER BY a.Name;

-- G2. A guide's crew (the workers it can dispatch as agent invocations)
SELECT a.Name AS Member, a.ModelId, gm.DisplayOrder, gm.MaxToolCallsPerInvocation
FROM GuideMembers gm
JOIN Assistants a ON a.Id = gm.AssistantId
WHERE gm.GuideId = @guideId
ORDER BY gm.DisplayOrder;

-- G3. Environment matrix: which project × assistant has which variables
--     (parse EnvironmentConfigJson on the client; NEVER print secret values —
--      encv2::... ciphertext or flag isSecret=true and show only the name)
SELECT p.Title AS ProjectTitle, a.Name AS AssistantName, a.Kind,
       e.EnvironmentConfigJson, e.Created, e.Updated
FROM ProjectAssistantEnvironments e
JOIN Projects p    ON p.Id = e.ProjectId
JOIN Assistants a  ON a.Id = e.AssistantId
ORDER BY p.Title, a.Name;

-- G4. Project → its guides (via notebooks), with env-override flag
SELECT p.Title AS ProjectTitle, a.Name AS GuideName,
       COUNT(n.Id) AS NotebooksByGuide,
       CASE WHEN e.EnvironmentConfigJson IS NOT NULL THEN 1 ELSE 0 END AS HasEnvOverride
FROM Projects p
JOIN Notebooks n ON n.ProjectId = p.Id
LEFT JOIN Assistants a ON a.Id = n.GuideId
LEFT JOIN ProjectAssistantEnvironments e ON e.ProjectId = p.Id AND e.AssistantId = a.Id
WHERE n.GuideId IS NOT NULL
GROUP BY p.Title, a.Name,
         CASE WHEN e.EnvironmentConfigJson IS NOT NULL THEN 1 ELSE 0 END
ORDER BY p.Title, a.Name;

-- G5. Published (embedded) guides and their limits
SELECT pg.FriendlyName, pg.AuthMode, pg.Active, pg.McpEnabled, pg.MaxTurns,
       pg.DailyChargeLimitUsd, pg.BillingPeriodChargeLimitUsd,
       a.Name AS GuideName, nb.Title AS EmbeddedInNotebook,
       p.Title AS InProject
FROM PublishedGuides pg
JOIN Assistants a  ON a.Id  = pg.GuideId
JOIN Notebooks nb  ON nb.Id = pg.NotebookId
JOIN Projects p    ON p.Id  = nb.ProjectId
ORDER BY pg.Created;
-- AuthMode: 0=Anonymous 1=Webhook 2=ApiKey 3=AppIdentity (ApiKeyHash only — no raw key)

-- G6. Spend by guide/assistant (who the bill attributes to)
SELECT a.Name, a.Kind, COUNT(*) AS Events, ROUND(SUM(u.ChargeUsd), 2) AS ChargeUsd
FROM UsageEvents u
JOIN Assistants a ON a.Id = u.AssistantId
WHERE u.Category IN (0, 8)
GROUP BY a.Name, a.Kind
ORDER BY ChargeUsd DESC;
```
