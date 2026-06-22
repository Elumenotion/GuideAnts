# GuideAnts Feature List Analysis Approach (Draft v1)

Status: Draft for review and iteration
Purpose: Define a repeatable way to build a compelling feature list for a large product without either overwhelming detail or underselling value.

## 1. Problem framing

GuideAnts is large enough that:

- A raw inventory feels like a dump.
- A short summary can hide meaningful differentiation.

This approach uses two complementary passes:

- Bottom-up evidence (database + API) for completeness and technical truth.
- Top-down evidence (UI) for user-visible value and narrative clarity.

Then we reconcile both into a layered feature list.

## 2. Target output (what this process should produce)

The final feature list should ship in three layers so we can tune depth by audience:

1. Value pillars (6-10): short, business-readable capability areas.
2. Core features (3-7 per pillar): concrete, user-outcome oriented capabilities.
3. Proof points (optional details): selected technical evidence that validates each feature without becoming a dump.

Each feature should map to both:

- User value statement (why this matters).
- Evidence anchors (where this exists in product reality: DB/API/UI).

## 3. Analysis method

### 3.1 Bottom-up pass (DB + API)

Goal: capture what the platform can truly do, including features that are not obvious from UI alone.

Sources in this repo:

- Data model: `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs`
- Data entities: `src/server/GuideAntsApi.DataModel/Models/`
- Evolution/history: `src/server/GuideAntsApi.DataModel/Migrations/`
- API surface: `src/server/GuideAntsApi/Endpoints/`
- Endpoint registration: `src/server/GuideAntsApi/Program.cs`
- Client API consumption: `src/client/src/services/api.ts`
- API spec snapshot: `guideants-swagger.json`

Extraction outputs:

- Domain entities and relationships (project, notebook, guide, assistant, publishing, usage, settings, runtime).
- Action verbs from endpoints (create/edit/publish/sync/transcribe/index/rebuild/manage).
- Operational capabilities surfaced via background and runtime flows.

Result format:

- Capability candidates grouped by domain, with evidence links and a confidence score (`high`, `medium`, `low`).

### 3.2 Top-down pass (UI)

Goal: capture what users can discover and do, and how the product tells its own story.

Sources in this repo:

- Route map: `src/client/src/components/AppContent.tsx`
- Primary pages: `src/client/src/pages/`
- Settings IA: `src/client/src/pages/Settings.tsx`, `src/client/src/pages/settings/components/SettingsTabNavigation.tsx`
- Feature-heavy UI areas: guides, assistants, notebook conversations, settings editors.

Extraction outputs:

- Major user journeys (authoring, collaboration, publishing, operations, administration).
- Screen-level feature inventory (what users can do from each route/tab).
- Discoverability signals (high-visibility vs buried capabilities).

Result format:

- UI feature candidates grouped by journey and entry point, each with user-facing language.

### 3.3 Reconciliation pass (the critical step)

Goal: merge both passes into one truth set and remove distortion.

For each candidate feature:

1. Merge duplicate wording from DB/API/UI.
2. Confirm minimum evidence: one technical anchor and one user anchor.
3. Assign narrative tier:
   - `headline`: differentiator or high-value core capability.
   - `supporting`: important but not top-level headline.
   - `proof`: technical depth kept available but not foregrounded.
4. Flag gaps:
   - `hidden strength`: strong backend capability with weak UI discoverability.
   - `ui-only claim risk`: UI suggests a capability with weak backend evidence.
   - `overlap`: multiple items describing same value.

## 4. Working artifact for review

Use a single matrix as the core review sheet:

| Feature ID | Pillar | User value statement | Primary UI entry points | API/DB evidence | Narrative tier | Notes |
|---|---|---|---|---|---|---|
| F-001 | Example pillar | Outcome-focused statement | Routes/tabs/screens | Entities/endpoints | headline/supporting/proof | Merge/gap notes |

This keeps detail available while preserving executive readability.

## 5. Editorial rules (to avoid dump vs undersell)

- Write feature names as outcomes, not implementation terms.
- Keep implementation terms in proof points only.
- Do not list every endpoint/entity as a feature.
- Do not keep claims that lack evidence in repository sources.
- Prefer breadth-first completeness first, then depth trimming.

## 6. Proposed review cadence

1. Pass A: Build raw candidate set from bottom-up and top-down analyses.
2. Pass B: Reconcile and collapse duplicates into a single capability map.
3. Pass C: Produce two views from the same map:
   - Executive feature list (concise).
   - Technical appendix (proof points and evidence anchors).
4. Pass D: Final language polish by target audience (buyers, technical evaluators, internal stakeholders).

## 7. Suggested next iteration

For the next step, we should run Pass A and deliver:

1. Initial pillar taxonomy.
2. First draft feature matrix (with evidence anchors).
3. A short list of likely “headline” differentiators to validate with you.
