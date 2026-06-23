# GuideAnts Client — Navigation & User Action Catalog

Action-level inventory of what a user can click, type, right-click, or trigger via keyboard in the GuideAnts web client (`src/client`). Organized by **screen → region → action**, with permission columns for each role.

**Last reviewed:** 2026-06-23 (against `src/client` route guards, sidebars, and header components).

## Permission legend

| Column | Role | Notes |
|--------|------|-------|
| **A** | Admin | Full app access |
| **C** | Contributor | `canEdit()` true; no admin-only surfaces |
| **R** | Reader | Read-only; editor routes blocked |
| **P** | Pending | Stuck on `/pending` |

`—` = action not available to that role.

### Code-level permission checks

| Check | Definition | Source |
|-------|------------|--------|
| `canEdit()` | `Admin` or `Contributor` | `ProjectContext` |
| `isOwner()` | `Admin` only (not per-project ownership) | `ProjectContext` |
| `requireEditor` | Blocks `Reader` from editor routes | `ProtectedRoute` |
| `requireAdmin` | Blocks non-`Admin` from admin routes | `ProtectedRoute` |

---

## Global shell (most authenticated screens)

### Header icon bar (`HeaderActionsBar`)

| # | Label / control | User action | Result | A | C | R | P |
|---|-----------------|-------------|--------|---|---|---|---|
| 1 | GuideAnts Guide | Click | Open right flyout chat panel | ✓ | ✓ | ✓ | — |
| 2 | New Project (folder+) | Click | Navigate `/new-project` | ✓ | ✓ | — | — |
| 3 | Usage (chart) | Click | Navigate `/usage` | ✓ | — | — | — |
| 4 | Setup Wizard (tool) | Click | Open **Add AI Services Wizard** modal | ✓ | — | — | — |
| 5 | System Guides (book) | Click | Navigate `/settings/system-guides` | ✓ | — | — | — |
| 6 | Home | Click | Navigate `/` | ✓ | ✓ | ✓ | — |
| 7 | Settings | Click | Navigate `/settings` (disabled when already on Settings) | ✓ | ✓ | ✓ | — |
| 8 | User (avatar) | Click | Toggle dropdown | ✓ | ✓ | ✓ | — |
| 9 | Sign Out (in dropdown) | Click | `logout()` → `/login` | ✓ | ✓ | ✓ | — |
| 10 | Tour (?) | Click | Start screen-specific guided tour | ✓ | ✓ | ✓ | — |
| 11 | More actions (⋯) | Click | Overflow menu for icons that don't fit | ✓ | ✓ | ✓ | — |

**Key files:** `HeaderActionsBar.tsx`, `GuideAntsGuideButton.tsx`, `HomeButton.tsx`, `SettingsButton.tsx`, `HeaderUserMenu.tsx`, `TourStartButton.tsx`

### Project layout header additions

| # | Label | User action | Result | A | C | R |
|---|-------|-------------|--------|---|---|---|
| 12 | Edit Project (pencil) | Click | Navigate `/projects/:id/edit` | ✓ | ✓ | — |
| 13 | Toggle sidebar (☰) | Click | Show/hide sidebar (mobile) | ✓ | ✓ | ✓ |

**Key file:** `ProjectLayout.tsx`

### Notebook layout header additions

| # | Label | User action | Result | A | C | R |
|---|-------|-------------|--------|---|---|---|
| 14 | Edit Notebook (pencil) | Click | Navigate `.../notebooks/:id/edit` | ✓ | ✓ | — |
| 15 | Back to Project (arrow) | Click | Navigate `/projects/:projectId` | ✓ | ✓ | ✓ |
| 16 | Notebook service toolbar | See [Notebook Service Toolbar](#notebook-service-toolbar-admin-only) | Admin-only center column | ✓ | — | — |

**Key file:** `NotebookLayout.tsx`

### Sidebar chrome (`SidebarContainer`)

| # | Label | User action | Result | A | C | R |
|---|-------|-------------|--------|---|---|---|
| 17 | Collapse sidebar (←) | Click | Collapse to 40px strip | ✓ | ✓ | ✓ |
| 18 | Expand sidebar (→) | Click | Restore sidebar width | ✓ | ✓ | ✓ |
| 19 | Resize handle | Drag | Resize sidebar 200–600px | ✓ | ✓ | ✓ |
| 20 | Close sidebar (×) | Click | Close mobile drawer | ✓ | ✓ | ✓ |

**Key file:** `SidebarContainer.tsx`

### GuideAnts Guide flyout

| # | Label | User action | Result | A | C | R |
|---|-------|-------------|--------|---|---|---|
| 21 | × | Click | Close flyout | ✓ | ✓ | ✓ |
| 22 | Backdrop | Click | Close flyout | ✓ | ✓ | ✓ |
| 23 | Escape | Key | Close flyout | ✓ | ✓ | ✓ |
| 24 | Embedded chat | Type + send | Chat with system guide | ✓ | ✓ | ✓ |
| 25 | Admin badge | — | Visual only; enables sandbox admin tools in chat bridge | ✓ | — | — |

**Key files:** `GuideAntsGuideFlyout.tsx`, `guideantsAppBridge.ts`

---

## Route map

**Router:** `AppContent.tsx` · **Guards:** `ProtectedRoute.tsx`

### Public

| Path | Page |
|------|------|
| `/login`, `/register` | Auth |
| `/oauth/callback`, `/redirect` | OAuth |
| `/terms`, `/privacy` | Legal |
| `/public/:friendlyName` | Public guide viewer |

### Authenticated

| Path | Page | Extra guard |
|------|------|-------------|
| `/` | Home | — |
| `/projects` | Projects list | — |
| `/conversations` | All conversations | — |
| `/usage` | Usage dashboard | No admin guard (link Admin-only on Home) |
| `/settings` | Settings | Non-admins: Personalization tab only |
| `/projects/:projectId` | Project workspace | — |
| `/projects/:projectId/notebooks/:notebookId` | Notebook workspace | — |
| `/projects/:projectId/notebooks/:notebookId/files/preview` | File preview | — |
| `/pending` | Pending approval | Pending role only |
| `/change-password` | Force password change | `mustChangePassword` only |

### Editor routes (`requireEditor` — Readers → `/`)

| Path | Page |
|------|------|
| `/new-project` | New project |
| `/projects/:projectId/edit` | Edit project |
| `/projects/:projectId/notebooks/:notebookId/edit` | Edit notebook |

### Admin-only routes (`requireAdmin`)

| Path | Page | Fallback |
|------|------|----------|
| `/settings/system-guides` | System guides workspace | `/settings` |
| `/projects/:projectId/guides` | Guides dashboard | `/` |
| `/projects/:projectId/guides/guide/new` | New guide editor | `/` |
| `/projects/:projectId/guides/guide/:guideId` | Edit guide | `/` |
| `/projects/:projectId/guides/guide/:guideId/usage` | Guide usage report | `/` |
| `/projects/:projectId/guides/assistant/new` | New assistant editor | `/` |
| `/projects/:projectId/guides/assistant/:assistantId` | Edit assistant | `/` |
| `/projects/:projectId/guides/assistant/:assistantId/usage` | Assistant usage report | `/` |

### Auth side-effects (`ProtectedRoute`)

- Unauthenticated → `/login?returnUrl=...`
- `Pending` → `/pending`
- `mustChangePassword` → `/change-password`

---

## Authentication screens

### Login (`/login`)

| Action | Result |
|--------|--------|
| Enter email + password | Form input |
| **Sign In** | `login()` → redirect `/`, `/pending`, `/change-password`, or `returnUrl` |
| **Create an account** link | `/register` |
| **Terms of Service** | `/terms` |
| **Privacy Policy** | `/privacy` |

### Register (`/register`)

| Action | Result |
|--------|--------|
| Name, Email, Password, Confirm | Inputs |
| **Create Account** | `register()` → post-auth redirect |
| **Sign in** link | `/login` |

### Pending (`/pending`)

| Action | Result | P only |
|--------|--------|--------|
| **Refresh Status** | `refresh()`; if approved → `/`, `/change-password`, or stay | ✓ |
| **Sign Out** | Logout → `/login` | ✓ |

### Change Password (`/change-password`)

| Action | Result |
|--------|--------|
| Current / New / Confirm password | Inputs |
| **Update Password** | `changePassword()` → `/` or `/pending` |
| **Sign Out** | Logout → `/login` |

---

## Home (`/`)

### Header actions

Global shell rows 1–11, plus Home-specific: **New Project** (A,C), **Usage** (A), **Setup Wizard** (A).

### Main content

| # | Label | User action | Result | A | C | R |
|---|-------|-------------|--------|---|---|---|
| 1 | **Quick Start** | Click | If recent notebook exists: create conversation + navigate notebook; else `api.quickStart.create()` full flow | ✓ | ✓ | — |
| 2 | Tab **Recent Conversations** | Click | Switch tab | ✓ | ✓ | ✓ |
| 3 | Tab **Recent Projects** | Click | Switch tab | ✓ | ✓ | ✓ |

### Recent Conversations tab

| # | Control | User action | Result | A | C | R |
|---|---------|-------------|--------|---|---|---|
| 4 | Search box | Type | Filter list (debounced) | ✓ | ✓ | ✓ |
| 5 | Sort dropdown | Select | Sort by date / project / notebook, asc/desc | ✓ | ✓ | ✓ |
| 6 | Page size | Select | 10 / 20 / 50 / 100 | ✓ | ✓ | ✓ |
| 7 | Conversation row | Click | `/projects/:pid/notebooks/:nid` with `conversationId` state | ✓ | ✓ | ✓ |
| 8 | **See all** | Click | `/conversations` with current query params | ✓ | ✓ | ✓ |
| 9 | **Previous** / **Next** / page # | Click | Paginate | ✓ | ✓ | ✓ |

### Recent Projects tab

| # | Control | User action | Result | A | C | R |
|---|---------|-------------|--------|---|---|---|
| 10 | Project row | Click | `/projects/:id` | ✓ | ✓ | ✓ |
| 11 | **⋯** (row menu trigger) | Click | Open context menu at cursor | ✓ | ✓ | ✓ |
| 12 | **See all** | Click | `/projects` | ✓ | ✓ | ✓ |

**Project row context menu:**

| Menu item | User action | Result | A | C | R |
|-----------|-------------|--------|---|---|---|
| **Edit** | Click | `/projects/:id/edit` (Readers blocked at route) | ✓ | ✓ | ✓* |
| **Copy** | Click | `api.projects.copyProject()` + refresh list | ✓ | ✓ | ✓* |
| **Delete** | Click | Confirm dialog → `api.projects.deleteProject()` | ✓ | ✓ | ✓* |

\*Menu is shown to all roles (`isUserOwner` always returns `true` in `Projects.tsx` / `RecentProjectsList.tsx`); server may reject mutations for Readers.

**Delete confirm dialog:** **Cancel** | **Delete**

**Keyboard:** `Escape` closes context menu

### Add AI Services Wizard (Admin only)

Multi-step modal: provider connection → models → optional services. Auto-opens on first launch when Admin has no connections/models. **Dismiss** or **Open Settings** from wizard.

**Key files:** `Home.tsx`, `AddAiServicesWizard`, `addAiServicesWizard/*`

---

## Projects list (`/projects`)

| # | Label | User action | Result | A | C | R |
|---|-------|-------------|--------|---|---|---|
| 1 | **New Project** | Click | `/new-project` | ✓ | ✓ | —* |
| 2 | Project row | Click | `/projects/:id` | ✓ | ✓ | ✓ |
| 3 | **⋯** → Edit/Copy/Delete | Same as Home project menu | ✓ | ✓ | ✓* |
| 4 | **Retry** (error state) | Click | Refetch projects | ✓ | ✓ | ✓ |

Header: Guide, Home, Settings, Tour only (no New Project icon in header).

---

## All Conversations (`/conversations`)

| # | Control | User action | Result | A | C | R |
|---|---------|-------------|--------|---|---|---|
| 1 | Search | Type + submit | Filter; synced to URL `?search=` | ✓ | ✓ | ✓ |
| 2 | Sort by | Select | `date` / `project` / `notebook` | ✓ | ✓ | ✓ |
| 3 | Sort order | Select | `asc` / `desc` | ✓ | ✓ | ✓ |
| 4 | Page size | Select | 10/20/50/100 (or auto) | ✓ | ✓ | ✓ |
| 5 | Table row | Click | Open notebook with conversation | ✓ | ✓ | ✓ |
| 6 | Pagination controls | Click | Change `?page=` | ✓ | ✓ | ✓ |

---

## Usage (`/usage`)

Discoverable from Home header for Admin only; **no route guard** — any authenticated user can navigate directly.

| # | Control | User action | Result | A | C | R |
|---|---------|-------------|--------|---|---|---|
| 1 | Range: 7d / 30d / 90d | Click | Refetch usage for range | ✓ | ✓ | ✓ |
| 2 | Bucket: Day / Week / Month | Click | Change time-series grouping | ✓ | ✓ | ✓ |
| 3 | Project card | Click | Drill into project usage | ✓ | ✓ | ✓ |
| 4 | **← All projects** | Click | Clear project drill-down | ✓ | ✓ | ✓ |
| 5 | Category breakdown | — | Read-only scroll | ✓ | ✓ | ✓ |

---

## Settings (`/settings`)

### Tab bar

| Tab | Visible to |
|-----|------------|
| Overview, Users, Connections, Models & Runtime, Services, Infrastructure, Telemetry | **Admin** |
| Personalization | **All authenticated** |

Non-admin landing on admin tab → forced back to **Personalization**.

**Key files:** `Settings.tsx`, `SettingsTabNavigation.tsx`

### Personalization tab (all roles)

| Action | Result |
|--------|--------|
| Edit name fields | Input |
| **Refresh** | Reload profile |
| **Reset** | Revert unsaved changes |
| **Save** | API user update |
| Change password fields + **Change Password** | Update password |
| **Sign Out** | Logout |

### Users tab (Admin)

| # | Control | User action | Result |
|---|---------|-------------|--------|
| 1 | Status filter | Select | `all` / `pending` / `active` / `inactive` |
| 2 | Role filter | Select | `all` / Pending / Reader / Contributor / Admin |
| 3 | **Refresh** | Click | Reload user list |
| 4 | Role dropdown (per row) | Select | Pick Reader / Contributor / Admin |
| 5 | **Approve user** (✓) | Click | Approve Pending user with selected role |
| 6 | **Update role** (edit icon) | Click | `api.adminUsers.changeRole()` |
| 7 | **Set password** (key icon) | Click | Open password modal |
| 8 | **Deactivate** / **Reactivate** (power icon) | Click | Confirm → deactivate/reactivate |

**Set password modal:** Password + Confirm → **Cancel** | **Set password** (min 8 chars, must match)

**Deactivate/Reactivate confirm:** **Cancel** | **Deactivate** / **Reactivate**

### Overview tab (Admin)

| Action | Result |
|--------|--------|
| **Refresh overview** / **Retry** | Reload summaries |
| Chat defaults model pickers + **Save chat defaults** | Update global chat model |
| Per-row **Open in Connections** / **Open in Services** / **Open Models & Runtime** | Deep-link to settings section |

### Connections / Models & Runtime / Services / Infrastructure / Telemetry (Admin)

Common action vocabulary per tab:

| Action pattern | Typical result |
|----------------|----------------|
| Section/item click | Select provider or service to edit |
| **Refresh** | Reload section data |
| **Reset** | Discard unsaved edits |
| **Save** | Persist configuration |
| **Test connection** / **Test** | Validate provider |
| **Add model** | Open `AddModelWizard` |
| **Delete** (model/profile/alias) | Confirm → delete |
| **Load** / **Unload** (Llama) | Start/stop local runtime |
| **Import** / **Export** (runtime profiles) | File pickers |

### Admin install progress banner

When model install in flight: **Open progress** → Models & Runtime catalog + wizard.

---

## Project workspace (`/projects/:projectId`)

**Key files:** `ProjectDetails.tsx`, `ProjectSidebar.tsx`, `FolderTree.tsx`, `ProjectLayout.tsx`

### Content pane — actions by selection

#### Default / no selection (project home)

| Action | Result | A | C | R |
|--------|--------|---|---|---|
| View homepage markdown | Read-only render of `homePageContentFileId` or default MD | ✓ | ✓ | ✓ |

#### Content file selected (`ContentFileContent`)

| # | Label | User action | Result | A | C | R |
|---|-------|-------------|--------|---|---|---|
| 1 | **Edit** | Click | Open full-screen markdown editor | ✓ | ✓ | — |
| 2 | **Download** | Click | Blob download | ✓ | ✓ | ✓ |
| 3 | **Delete** | Click | Confirm → delete file | ✓ | ✓ | — |
| 4 | **History** | Click | Open version history drawer | ✓ | ✓ | ✓ |
| 5 | Tab **Original** | Click | Show raw file | ✓ | ✓ | ✓ |
| 6 | Tab **Extracted text** / **Markdown** | Click | Show converted view | ✓ | ✓ | ✓ |
| 7 | **Retry** | Click | Reload failed conversion | ✓ | ✓ | ✓ |

**History drawer:** backdrop click close, **Refresh**, **Close**

**Markdown editor:** **Save** (Ctrl+Enter) | **Cancel** (Esc)

#### Link selected (`LinkContent`)

*Sidebar Links section is UI-hidden; content reachable only if a link already exists.*

| # | Label | User action | Result | A | C | R |
|---|-------|-------------|--------|---|---|---|
| 1 | URL button | Click | Open URL in browser | ✓ | ✓ | ✓ |
| 2 | **Open in new tab** | Click | `window.open(url)` | ✓ | ✓ | ✓ |
| 3 | **Copy URL** | Click | Clipboard | ✓ | ✓ | ✓ |
| 4 | **Edit Link** | Click | Inline edit mode | ✓ | ✓ | — |
| 5 | **Save** / **Cancel** | Click | Persist or abort edit | ✓ | ✓ | — |
| 6 | **Delete** | Click | Confirm → delete link | ✓ | ✓ | — |

#### Add link form

| Action | Result | A | C | R |
|--------|--------|---|---|---|
| URL input + **Add Link** | `api.projects.addLink()` | ✓ | ✓ | — |
| **Cancel** | Close form | ✓ | ✓ | — |
| **Open in new tab** (preview) | Open entered URL | ✓ | ✓ | — |

#### Add folder form

| Action | Result | A | C | R |
|--------|--------|---|---|---|
| Folder name + parent pick + **Create Folder** | Create folder API | ✓ | ✓ | — |
| **Cancel** | Close form | ✓ | ✓ | — |

#### Guide Authorization template (`ProjectGuideAuthContent`) — Admin only (`isOwner()`)

| Action | Result | A | C | R |
|--------|--------|---|---|---|
| OAuth override fields edit | Input | ✓ | — | — |
| **Test OAuth** (per provider) | Test connection | ✓ | — | — |
| **Save OAuth override** | Persist override | ✓ | — | — |
| **Clear OAuth override** | Reset to default | ✓ | — | — |
| API key fields edit | Input | ✓ | — | — |
| **Save API keys** | Persist keys | ✓ | — | — |

#### Artifact selected (`ArtifactContent`)

Placeholder UI with **Edit** / **View** buttons (limited implementation).

---

## Project sidebar — every action

**Key file:** `ProjectSidebar.tsx` · Folder tree: `FolderTree.tsx` · Section chrome: `SidebarSection.tsx`

### Search bar

| Action | Result | A | C | R |
|--------|--------|---|---|---|
| Type in search | Filter notebooks + files | ✓ | ✓ | ✓ |
| **×** clear | Clear search | ✓ | ✓ | ✓ |

### Section: Notebooks

**Section header actions:**

| # | Control | User action | Result | A | C | R |
|---|---------|-------------|--------|---|---|---|
| 1 | Section title / chevron | Click | Expand/collapse | ✓ | ✓ | ✓ |
| 2 | **Recent** sort | Click | Sort by last activity | ✓ | ✓ | ✓ |
| 3 | **A-Z** sort | Click | Sort alphabetically | ✓ | ✓ | ✓ |
| 4 | **+** (blue) | Click | Open **Create Notebook** dialog | ✓ | ✓ | — |

**Notebook list item interactions:**

| Interaction | Result | A | C | R |
|-------------|--------|---|---|---|
| Single click | Select notebook in sidebar | ✓ | ✓ | ✓ |
| Double-click / mobile tap | Navigate `/projects/:pid/notebooks/:nid` | ✓ | ✓ | ✓ |
| Ctrl/Cmd+click | Multi-select | ✓ | ✓ | — |
| Shift+click | Range select | ✓ | ✓ | — |
| Right-click / long-press | Open context menu | ✓ | ✓ | — |
| Inline rename (F2) | Edit title in place | ✓ | ✓ | — |

**List keyboard** (notebooks section focused, `useListKeyboardNavigation`):

| Key | Action | A | C | R |
|-----|--------|---|---|---|
| ↑ / ↓ | Move focus | ✓ | ✓ | ✓ |
| Home / End | First / last item | ✓ | ✓ | ✓ |
| Enter | Open notebook | ✓ | ✓ | ✓ |
| Space | Toggle selection | ✓ | ✓ | — |
| Shift+↑/↓ | Extend selection | ✓ | ✓ | — |
| Delete | Delete selected (confirm) | ✓ | ✓ | — |
| F2 | Rename selected | ✓ | ✓ | — |
| Ctrl/Cmd+A | Select all | ✓ | ✓ | — |
| Escape | Clear selection | ✓ | ✓ | — |

**Notebook context menu — single selection:**

| Item | API / navigation | A | C | R |
|------|------------------|---|---|---|
| **Copy** | `api.projects.copyNotebook()` | ✓ | ✓ | — |
| **Rename** | Inline edit → update | ✓ | ✓ | — |
| **Delete** | Confirm → delete | ✓ | ✓ | — |

**Notebook context menu — multi-selection:**

| Item | Result | A | C | R |
|------|--------|---|---|---|
| **Delete N Notebooks** | Confirm batch delete | ✓ | ✓ | — |

### Create Notebook dialog

| Control | User action | A | C | R |
|---------|-------------|---|---|---|
| Title | Type notebook name | ✓ | ✓ | — |
| Description (optional) | Type up to 1000 chars | ✓ | ✓ | — |
| Guide search | Filter template list | ✓ | ✓ | — |
| Sort guides | A–Z / Z–A | ✓ | ✓ | — |
| Guide template card | Click to select radio | ✓ | ✓ | — |
| **Cancel** | Close dialog | ✓ | ✓ | — |
| **Create** | Create notebook → navigate | ✓ | ✓ | — |

When opened from file context: shows "N files will be copied" notice.

### Section: Files (`FolderTree`)

**Section header:**

| # | Control | User action | Result | A | C | R |
|---|---------|-------------|--------|---|---|---|
| 1 | Chevron | Click | Expand/collapse | ✓ | ✓ | ✓ |
| 2 | **Upload** (blue ↑) | Click | Open **Upload Files** dialog | ✓ | ✓ | — |

**Folder row (hover, when `canEdit`):**

| Control | Action | A | C | R |
|---------|--------|---|---|---|
| Chevron | Expand/collapse folder | ✓ | ✓ | ✓ |
| **+** on folder | Create subfolder inline | ✓ | ✓ | — |
| **↑** on folder | Upload to this folder | ✓ | ✓ | — |
| Click folder name | Select folder | ✓ | ✓ | ✓ |
| Drag file onto folder | Move file (`onMoveFile`) | ✓ | ✓ | — |

**Folder context menu — single:**

| Item | Result | A | C | R |
|------|--------|---|---|---|
| **New Markdown File** | Open full-screen MD editor (create) | ✓ | ✓ | — |
| **Rename** | Inline folder rename | ✓ | ✓ | — |
| **Create Subfolder** | Inline subfolder input | ✓ | ✓ | — |
| **Upload Files** | Open upload dialog scoped to folder | ✓ | ✓ | — |
| **Delete** | Confirm delete (disabled if children) | ✓ | ✓ | — |

**File context menu — single:**

| Item | Result | A | C | R |
|------|--------|---|---|---|
| **Edit** | Full-screen MD editor (`.md` only) | ✓ | ✓ | — |
| **Create Notebook from File** | Create Notebook dialog | ✓ | ✓ | — |
| **Set Project Home Page** / **Clear as Home Page** | Set/clear homepage | ✓ | ✓ | — |
| **Rename** | Inline rename | ✓ | ✓ | — |
| **Download** | Download blob | ✓ | ✓ | ✓ |
| **Delete** | Confirm delete | ✓ | ✓ | — |

**File context menu — multi-select:**

| Item | Result | A | C | R |
|------|--------|---|---|---|
| **Create Notebook from N Files** | Create Notebook dialog with files | ✓ | ✓ | — |
| **Download N Items** | Sequential downloads | ✓ | ✓ | ✓ |
| **Delete N Items** | Confirm batch delete | ✓ | ✓ | — |

**Files section keyboard** (`useSidebarKeyboardShortcuts`, when Files section active):

| Key | Action | A | C | R |
|-----|--------|---|---|---|
| Delete | Delete selection | ✓ | ✓ | — |
| F2 | Rename | ✓ | ✓ | — |
| Ctrl/Cmd+A | Select all | ✓ | ✓ | — |
| Escape | Clear selection | ✓ | ✓ | — |
| Ctrl/Cmd+C | Copy | ✓ | ✓ | — |
| Ctrl/Cmd+V | Paste | ✓ | ✓ | — |

### Section: Guide Authorization (Admin only, when templates exist)

| Action | Result | A | C | R |
|--------|--------|---|---|---|
| Click template item | Load `ProjectGuideAuthContent` in pane | ✓ | — | — |
| Chevron | Expand/collapse | ✓ | — | — |

### Section: Guides (Admin only)

| Action | Result | A | C | R |
|--------|--------|---|---|---|
| Click **Guides** button | Navigate `/projects/:pid/guides` | ✓ | — | — |

### Section: Links — UI-hidden

Wrapped in `{false && ...}` in `ProjectSidebar.tsx`. Code exists for **+** add link, click link, context **Delete** — not reachable in current UI.

---

## Notebook workspace (`/projects/:projectId/notebooks/:notebookId`)

**Key files:** `NotebookDetails.tsx`, `NotebookSidebar.tsx`, `NotebookFolderTree.tsx`

### Notebook sidebar

Structure mirrors project sidebar for **Conversations** and **Files**. Links section also hidden.

### Section: Conversations

**Section header:**

| Control | Action | A | C | R |
|---------|--------|---|---|---|
| **+** | Open **Create Conversation** dialog | ✓ | ✓ | — |
| **Recent** / **A-Z** | Sort toggles | ✓ | ✓ | ✓ |
| Chevron | Expand/collapse | ✓ | ✓ | ✓ |

**Create Conversation dialog:**

| Action | Result | A | C | R |
|--------|--------|---|---|---|
| Title input | Type conversation name | ✓ | ✓ | — |
| **Create** | `createConversation()` + select new | ✓ | ✓ | — |
| **Cancel** | Close | ✓ | ✓ | — |

**Conversation context menu — single** (only when `canEdit`):

| Item | Result | A | C | R |
|------|--------|---|---|---|
| **Copy** | Duplicate conversation | ✓ | ✓ | — |
| **Save Conversation** | Export as markdown file | ✓ | ✓ | — |
| **Rename** | Inline rename | ✓ | ✓ | — |
| **Delete** | Confirm delete | ✓ | ✓ | — |

**Conversation context menu — multi:**

| Item | Result | A | C | R |
|------|--------|---|---|---|
| **Delete N Conversations** | Confirm batch delete | ✓ | ✓ | — |

**Conversation keyboard shortcuts:** Delete, F2, Ctrl+A, Escape (same hook as notebooks).

### Section: Files (`NotebookFolderTree`)

Same folder/file menus as project tree, plus notebook-specific items.

**Folder context menu additions (Admin only, notebook root):**

| Item | Result | A | C | R |
|------|--------|---|---|---|
| **Map host folder here** | Open `MapHostFolderDialog` | ✓ | — | — |
| **Check mapped folders** | Reconcile mounts API | ✓ | — | — |

**Folder context menu additions (Admin only, mount root):**

| Item | Result | A | C | R |
|------|--------|---|---|---|
| **Remove mapped folder** | Confirm remove mapping | ✓ | — | — |
| **Show apply command** | Display CLI apply command | ✓ | — | — |
| **Show remove command** | Display CLI remove command | ✓ | — | — |

Linked/mount folders: Rename, New Markdown, Create Subfolder, Upload, Delete suppressed on linked paths.

**File context menu — single:**

| Item | Notes | A | C | R |
|------|-------|---|---|---|
| **Edit** | Markdown; not linked | ✓ | ✓ | — |
| **Preview** | Overlay or preview route | ✓ | ✓ | ✓ |
| **Publish to Project** | `PublishToProjectDialog` | ✓ | ✓ | — |
| **Download** | All files | ✓ | ✓ | ✓ |
| **Rename** | Not linked | ✓ | ✓ | — |
| **Set as Notebook Home Page** / **Clear as Home Page** | | ✓ | ✓ | — |
| **Delete** / **Delete on host** | Host-mount special label | ✓ | ✓ | — |
| *Linked files are read-only here* | Notice only | ✓ | ✓ | ✓ |

**File context menu — multi:**

| Item | A | C | R |
|------|---|---|---|
| **Publish N Files to Project** | ✓ | ✓ | — |
| **Download N Files** | ✓ | ✓ | ✓ |
| **Delete N Items** | ✓ | ✓ | — |
| Linked read-only notice | ✓ | ✓ | ✓ |

---

## Chat area (notebook main pane)

**Key files:** `ConversationPanel.tsx`, `ConversationHeader.tsx`, `DraftUserCell.tsx`, `UserCell.tsx`, `AssistantCell.tsx`, `LexicalToolbar.tsx`

### Conversation header

| # | Label | User action | Result | A | C | R |
|---|-------|-------------|--------|---|---|---|
| 1 | **Stop** / **Stopping...** | Click | `cancelStream()` while streaming | ✓ | ✓ | ✓ |
| 2 | **+** (mobile only) | Click | Create "New Conversation" | ✓ | ✓ | — |
| 3 | Assistant selector dropdown | Click → pick assistant | `setSelectedAssistant()` | ✓ | ✓ | —* |

\*Selector visible to Readers but `disabled={!canEdit}`.

### Composer (`DraftUserCell`)

| # | Control | User action | Result | A | C | R |
|---|---------|-------------|--------|---|---|---|
| 1 | Message editor | Type rich text / markdown | Compose message | ✓ | ✓ | — |
| 2 | **@** mention | Type `@` | Assistant mention picker | ✓ | ✓ | — |
| 3 | Attachment chips | Click file chip | Preview attachment | ✓ | ✓ | ✓ |
| 4 | **×** on pending attachment | Click | Remove pending attachment | ✓ | ✓ | — |
| 5 | **Take photo** (camera) | Click | Open `CameraCapture` modal | ✓ | ✓ | — |
| 6 | **Microphone** | Click/hold | Speech-to-text record | ✓ | ✓ | — |
| 7 | **Send** | Click | Submit message | ✓ | ✓ | — |
| 8 | **Full screen** (toolbar) | Click | Expand composer | ✓ | ✓ | — |
| 9 | Ctrl/Cmd+Enter | Key | Send | ✓ | ✓ | — |
| 10 | Escape | Key | Cancel draft | ✓ | ✓ | — |

**Lexical toolbar:** Bold (Ctrl+B), Italic (Ctrl+I), Strikethrough, Inline code, Headings dropdown, Bullet list, Numbered list, Quote, Insert link, Insert image, Insert audio, Insert video, Insert table, Toggle markdown source (Ctrl+M), Full screen, Cancel (Esc), Submit (Ctrl+Enter).

### Sent user message (`UserCell`)

| Label | Action | A | C | R |
|-------|--------|---|---|---|
| Attachment chip | Click | Preview file | ✓ | ✓ | ✓ |
| **Full screen** | Click | Expand message | ✓ | ✓ | ✓ |
| **Undo last turn** | Click | Confirm → undo last exchange | ✓ | ✓ | — |

**Undo confirm:** **Cancel** | **Undo Last Turn**

### Assistant message (`AssistantCell`)

| Label | Action | A | C | R |
|-------|--------|---|---|---|
| **Save to notebook** | Click | `SaveAssistantContentDialog` | ✓ | ✓ | — |
| **Edit message** | Click | Edit assistant output (last message only) | ✓ | ✓ | — |
| **Copy to clipboard** | Click | Copy message text | ✓ | ✓ | ✓ |
| **Full screen** | Click | Expand message | ✓ | ✓ | ✓ |
| **View original** (avatar, edited msgs) | Click | Show original vs edited | ✓ | ✓ | ✓ |
| **Show diff** toggle | Click | Toggle diff view | ✓ | ✓ | ✓ |
| File pills (created/modified) | Click | Preview turn file | ✓ | ✓ | ✓ |
| Tool call row (mobile) | Tap | Expand/collapse tool details | ✓ | ✓ | ✓ |
| Workflow section chevron | Click | Show/hide workflow | ✓ | ✓ | ✓ |

### Cell list

| Control | Action |
|---------|--------|
| **Scroll to bottom** (floating) | Scroll chat to latest message |

---

## Notebook Service Toolbar (Admin only)

**Key files:** `NotebookServiceToolbar.tsx`, `ChatToolbarPanel.tsx`, `ImageToolbarPanel.tsx`, `TtsToolbarPanel.tsx`, `AsrToolbarPanel.tsx`

Toolbar buttons: **Chat**, **Image generation**, **TTS**, **ASR** — each opens popover (desktop) or bottom sheet (mobile).

### Chat panel actions

| Action | Result |
|--------|--------|
| **Override all chat models** checkbox | Toggle global model override |
| Model option button | Set global model (when override on) |
| **Load model** / **Loaded** / **Switching...** | `loadLlamaRuntime()` poll until ready |
| **Unload** | Confirm → `unloadLlamaRuntime()` |
| **Open Settings** (gear) | Navigate `/settings` |

Image/TTS/ASR panels: service-specific load/unload, model pickers, **Open Settings**.

**Escape** closes open panel/sheet.

---

## File preview (`FilePreviewOverlay` / `/files/preview`)

| Label | Action | A | C | R |
|-------|--------|---|---|---|
| **Edit** | Open markdown editor | ✓ | ✓ | — |
| **Open in new window** | Popup preview | ✓ | ✓ | ✓ |
| **Download** | Download file | ✓ | ✓ | ✓ |
| **Full screen** / **Exit full screen** | Toggle | ✓ | ✓ | ✓ |
| **Close** | Dismiss | ✓ | ✓ | ✓ |
| Backdrop click | Close | ✓ | ✓ | ✓ |
| Tab **Original** / **Markdown** | Switch view | ✓ | ✓ | ✓ |
| **Retry** | Reload content | ✓ | ✓ | ✓ |
| Escape | Close (when not editing) | ✓ | ✓ | ✓ |

---

## Guides area (Admin routes only)

**Key files:** `GuidesDashboard.tsx`, `GuidesHeader.tsx`, `GuideCard.tsx`, `AssistantCard.tsx`, `GuideEditor.tsx`, `AssistantEditor.tsx`, `BaseEntityEditor.tsx`

### Guides dashboard

| # | Label | Action | Result |
|---|-------|--------|--------|
| 1 | **Guides** tab | Click | `?tab=guides` |
| 2 | **Assistants** tab | Click | `?tab=assistants` |
| 3 | Search box | Type | Filter cards |
| 4 | **Import** (.zip) | Click | File picker → import |
| 5 | **Create Guide** | Click | `/guides/guide/new` |
| 6 | **Create Assistant** | Click | `/guides/assistant/new` |

**Guide card icon actions:**

| Icon | Title | Action |
|------|-------|--------|
| Pencil | Edit | Navigate guide editor |
| Upload | Publish / Manage Publishing | `PublishGuideDialog` |
| Chart | Usage Report | `/guides/guide/:id/usage` |
| Download | Export | Download guide zip |
| Trash | Delete | Confirm delete |
| Published badge click | Manage / Reactivate | Publish dialog |

**Assistant card:** Edit, Usage Report, Export, Delete (no publish).

### Publish Guide dialog

Tabs: General, Interface, Features, Limits, Auth, APIs, MCP and Skills

| Button | Action |
|--------|--------|
| **Cancel** / **Close** | Dismiss |
| **Publish Guide** | Create published guide + notebook |
| **Save Changes** | Update published config |
| **Deactivate** | Confirm → deactivate |
| **Reactivate Guide** | Reactivate |

### Guide / Assistant editor

**Header:**

| Label | Action |
|-------|--------|
| **← Back to Guides/Assistants** | Navigate back (unsaved confirm if dirty) |
| **Export** | Download zip (edit mode) |
| **Cancel** (X) | Back with unsaved dialog |
| **Save Guide** / **Save Assistant** | Persist entity |

**Main tabs** (`?tab=`):

| Tab | Guide | Assistant |
|-----|-------|-----------|
| General | ✓ | ✓ |
| Configuration | ✓ | ✓ |
| Tools | ✓ | ✓ |
| Files | ✓ | ✓ |
| Environment | ✓ | ✓ |
| Crew | ✓ | — |
| Auth | ✓ | — |

**Unsaved changes dialog:** **Cancel** | **Leave without saving**

### Guide usage report

| Action | Result |
|--------|--------|
| **← Back** | Return to guides dashboard tab |
| Range 7d/30d/90d | Filter |
| **Tokens** / **Cost** chart tabs | Switch metric |
| Metric toggles | Direct vs total |
| API source filter | All / internal / external |
| Conversation row click | Open detail panel |
| **Load more** | Paginate |

### System Guides workspace (`/settings/system-guides`)

Loads system project via `api.systemGuide.getWorkspace()` then renders **ProjectDetails** for that project — all project workspace actions apply.

**Error screen:** **Retry**, **Back** → `/settings`

---

## Edit forms

### New Project (`/new-project`)

| Action | Result | A | C | R |
|--------|--------|---|---|---|
| Title, description inputs | Type | ✓ | ✓ | — |
| **Create Project** | `api.projects.create()` → navigate project | ✓ | ✓ | — |
| **Cancel** | Navigate `/` | ✓ | ✓ | — |

### Edit Project / Edit Notebook

| Action | Result | A | C | R |
|--------|--------|---|---|---|
| Form fields (title, description, collaborators on project) | Edit | ✓ | ✓ | — |
| **Save** | Update API → navigate back | ✓ | ✓ | — |
| **Cancel** | Navigate back without save | ✓ | ✓ | — |

---

## Admin vs non-admin — action delta summary

### Actions only Admins can perform

- Home: **Usage**, **Setup Wizard**, first-launch wizard
- Settings: all tabs except Personalization; **System Guides** header link; user admin actions; all provider/model/service config
- Project sidebar: **Guides** nav button; **Guide Authorization** section + edit actions in pane
- Notebook: **Notebook Service Toolbar** (all panel actions); host-folder context menu items
- All `/guides/*` and `/settings/system-guides` routes and their internal actions
- GuideAnts Guide: Admin badge + sandbox admin chat tools

### Actions Contributors share with Admins

- Quick Start, New Project, all editor routes
- Full project/notebook sidebar CRUD (notebooks, files, folders, conversations)
- All context menus except host-mount items
- Chat compose, send, assistant change, message edit/undo/save
- Content pane file edit/delete/download/history
- Personalization settings only

### Actions Readers can perform

- Navigate and view projects, notebooks, conversations, files
- Download files; preview files and attachments
- Copy assistant messages; full-screen messages
- View usage page if URL known (no Home link)
- Personalization settings only
- **Cannot:** any sidebar **+** / upload / context menu; compose chat; edit/delete anything; editor routes; guides; admin settings

### Pending users

Only **Refresh Status** and **Sign Out** on `/pending`.

---

## Unreachable / dead UI

| Feature | Status |
|---------|--------|
| Project sidebar **Links** section | Wrapped in `{false && ...}` |
| Notebook sidebar **Links** section | Same |
| Collaborators sidebar section | Referenced in search placeholder only; no sidebar section |
| Artifacts sidebar section | `ProjectDetails` handles `artifacts` type but no sidebar entry |

---

## Implementation notes

1. **`isCurrentUserOwner` / `isOwner()` = Admin role**, not project ownership — controls Guides link and Guide Authorization.
2. **Project list context menus** use `isUserOwner()` which always returns `true` — no client-side role filter.
3. **`/usage`** has no `requireAdmin` guard — only the Home header link is admin-gated.
4. **Contributor vs Admin** differs mainly in admin routes, settings tabs, Guides sidebar entry, notebook service toolbar, host-folder mapping, first-launch AI setup wizard, and GuideAnts Guide admin bridge tools.

---

## Key source files

| Area | Path |
|------|------|
| Routes | `src/client/src/components/AppContent.tsx` |
| Guards | `src/client/src/components/ProtectedRoute.tsx` |
| Permissions | `src/client/src/contexts/ProjectContext.tsx` |
| Home | `src/client/src/pages/Home.tsx` |
| Settings | `src/client/src/pages/Settings.tsx` |
| Settings tabs | `src/client/src/pages/settings/components/SettingsTabNavigation.tsx` |
| Project page | `src/client/src/pages/ProjectDetails.tsx` |
| Notebook page | `src/client/src/pages/NotebookDetails.tsx` |
| Project sidebar | `src/client/src/components/project/sidebar/ProjectSidebar.tsx` |
| Project folder tree | `src/client/src/components/project/sidebar/FolderTree.tsx` |
| Notebook sidebar | `src/client/src/components/notebook/sidebar/NotebookSidebar.tsx` |
| Notebook folder tree | `src/client/src/components/notebook/sidebar/NotebookFolderTree.tsx` |
| Sidebar keyboard | `src/client/src/hooks/useSidebarKeyboardShortcuts.ts` |
| List keyboard | `src/client/src/hooks/useListKeyboardNavigation.ts` |
| Guides dashboard | `src/client/src/pages/GuidesDashboard.tsx` |
| Guide editor | `src/client/src/components/guides/editor/BaseEntityEditor.tsx` |
| Layouts | `src/client/src/components/layouts/ProjectLayout.tsx`, `NotebookLayout.tsx`, `SidebarContainer.tsx` |

---

## Future expansion

Per-field actions inside Settings provider forms and Guide editor tabs (hundreds of individual inputs/buttons) are not enumerated here. Candidate follow-up sections:

- Settings → Connections (per-provider forms)
- Settings → Models & Runtime (catalog wizard, Llama inventory)
- Guide Editor → Tools / Crew tabs
