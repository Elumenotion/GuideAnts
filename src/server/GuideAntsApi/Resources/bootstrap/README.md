Bootstrap resources seeded on first startup.

Seeding is idempotent for most entities: if the entity already exists in the
database the seed is skipped and user modifications are not overwritten.

**Exception — GuideAnts system guides** (`guideants-guide`, `guideants-guide-admin`):
`GuideAntsSystemSeeder` re-imports bootstrap content on every startup so
instructions, OpenAPI tools, and related guide files stay aligned with the
repo. Published-guide interface settings for these rows are also repaired to
the canonical in-app defaults on each seed.

## Folders

### `guides/` and `assistants/`

Required guides and their crew member assistants. Each subfolder is a
folder-based export (contains `manifest.json` plus the normal
export/import file layout). ZIP archives (`*.zip`) are also supported.

Assistants are imported before guides so crew members exist when guide
import links them.

Seeds omit model-specific fields so they inherit the operator's
configured default chat model (`ChatDefaults`).

To add a new seed: export the guide or assistant from the running system
(`GET /api/guides/{id}/export` or `GET /api/assistants/{id}/export`),
extract the ZIP into a named subfolder here, and remove any `model`,
`defaultModel`, `temperature`, `top_p`, and `reasoning_effort` fields
from `manifest.json`.

### `runtime-profiles/`

Llama-cpp runtime profile templates (R-6.7, R-8.1). One JSON file per
profile keyed by `profileId`. Seeded directly into the `RuntimeProfiles`
table if no row with that ID exists.

Current templates: `qwen3_5`, `qwen3_6`, `gemma4`.

### `provider-stack-profiles/`

Provider stack profile definitions used by the Add AI Services wizard.
These are runtime product configuration, not database-seeded.
