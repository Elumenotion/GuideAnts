# Provider Stack Profiles Apply Payload

Last updated: 2026-04-29

## Purpose

This document defines the provider profile data used to **apply configuration**
after the operator enters required values in the Add AI Services Wizard.

The data is for writing existing Settings/Connections/Models/Services state.
It is not for constructing wizard UI behavior.

## In Scope

- Profile content that maps user-provided values into existing settings fields.
- Profile content that defines model rows to create.
- Profile content that defines service provider/mode assignments to apply.
- Profile content that can be reused by any entry point (wizard or manual run).

## Out Of Scope

- Wizard steps, page structure, labels, or control flow.
- Auto-open logic, dismissal logic, completion thresholds, or launch criteria.
- Readiness/status diagnostics in profile data.
- Editorial or explanatory payload fields (for example `notes`, `knownGaps`,
  `state`).

## Runtime Meaning

When a profile is selected and inputs are provided, the system uses the profile
payload to perform deterministic writes to:

1. Settings sections (`/api/settings/sections/{sectionName}`).
2. Models catalog (`/api/settings/models` or unified add endpoint).
3. Service routing/provider selections (service endpoints/sections already in
   use by the current Settings system).

No separate parallel configuration system is introduced.

## Data Location

Profile files live in:

`src/server/GuideAntsApi/Resources/bootstrap/provider-stack-profiles/`

One JSON file per profile. Discovery is by `*.json` in that folder.

## Contract (Apply Payload Only)

Each profile JSON file uses this contract:

- `schemaVersion`: number
- `profileId`: string
- `displayName`: string
- `settingsValues`: object of section-to-fields that the apply step writes:
  - `settingsValues.<SectionName>.<FieldName> = <literal or token string>`
- `connectionDefinitions`: array of
  - `sectionName`: string
  - `requiredFields`: string[]
- `modelDefinitions`: array of
  - `modelId`: string
  - `displayName`: string
  - `provider`: string
  - `isActive`: boolean
  - `description`: string (optional)
  - `reasoningChoicesJson`: string (optional)
- `serviceDefaults`: array of
  - `serviceId`: `Embeddings | ImageGeneration | SpeechTranscription | SpeechSynthesis | DocumentIntelligence`
  - `providerId`: string
  - `setAsDefault`: boolean
  - `providerFields`: object of direct provider field writes (`fieldName -> value`)
- `runtimeDependencies`: array of
  - `key`: string
  - `required`: boolean

Token convention:

- Values collected from users are represented as token strings like
  `{{azure-openai-api-key}}`.
- Tokens are resolved by the caller before executing writes.
- Tokens are not UI metadata; they are placeholders in apply payload values.

## Grounding Rules

All identifiers in profiles must match existing code constants/contracts:

- settings section names
- service IDs
- provider IDs
- model provider IDs
- mode IDs (where applicable)

If an ID is not present in current client/server contracts, it must not be
added to profile data.

## Non-Negotiable Constraint

Provider profile JSON is **data to apply configuration**. It is not wizard
control data.
