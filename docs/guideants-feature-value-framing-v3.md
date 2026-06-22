# GuideAnts Feature Value Framing (Draft v3)

Status: Draft for discussion
Date: 2026-06-08
Builds on: `docs/guideants-feature-value-framing-v2.md`

## 1. Core positioning

GuideAnts should be framed as more than an AI chat interface and more than an internal workspace.

Recommended positioning:

> GuideAnts is a platform for creating governed AI work products: teams build reusable guides inside structured workspaces, then share, embed, integrate, and operate them as controlled AI experiences.

This wording matters because GuideAnts has two connected value arcs:

- Internal work becomes reusable organizational capability.
- Reusable capability can become a product surface inside another site, report, portal, or application.

The Power BI-style demo and the `guideants` package are important examples because they show the product creation path: a published guide can live inside another reporting experience, not merely inside GuideAnts itself.

## 2. Product creation model

GuideAnts product creation should be described as a lifecycle:

| Stage | What the user is doing | GuideAnts value |
|---|---|---|
| Build | Create a guide or assistant from instructions, files, tools, model choices, and workflow design. | Expertise becomes a reusable asset instead of a one-off prompt. |
| Ground | Attach work to projects, notebooks, files, markdown extraction, context options, and generated artifacts. | The AI product has durable context and source material. |
| Validate | Test the guide in notebooks, conversations, and internal workflows. | Teams can refine behavior before external exposure. |
| Share | Publish with a friendly URL or controlled access pattern. | The workflow becomes available beyond the authoring environment. |
| Embed | Use the `guideants` web component to place a published guide inside another application. | The AI experience becomes part of a domain-specific product surface. |
| Integrate | Use public guide APIs, client context, attachments, and published conversation flows. | Existing apps and reports can call into the guide rather than redirect users elsewhere. |
| Govern | Apply auth, limits, retention, usage analytics, observability, and runtime controls. | The published AI product remains operable and accountable. |

## 3. Revised value pillars

| Pillar | Value promise | What to emphasize |
|---|---|---|
| Governed AI Workspaces | Keep AI work organized, contextual, and durable. | Projects, notebooks, files, links, conversations, artifacts, home pages, versioning, lineage. |
| Reusable Expertise | Convert organizational know-how into reusable guide and assistant assets. | Instructions, tools, files, context options, crew patterns, conversation starters, imports/exports. |
| AI Product Creation | Turn internal guides into shareable, embeddable, and integrated AI experiences. | Published guides, friendly URLs, `guideants` web component, public guide APIs, domain app/report integration. |
| Enterprise Governance | Preserve provenance, manage lifecycle, and support audit/compliance needs. | File versioning, lineage events, markdown shadows, notebook sync, retention cleanup, role controls. |
| Cost and Usage Control | Attribute and limit AI activity before it becomes operational or financial risk. | Usage events, assistant/message/invocation attribution, charge tracking, guide analytics, daily limits. |
| Observability and Operations | Give operators tools to tune, debug, and prove what happened. | Telemetry presets, structured categories, infrastructure probes, readiness checks, runtime status. |
| Secure Modular Runtime | Isolate risky or heavyweight capabilities behind scoped internal services. | Sandbox agent, token-gated API-to-agent calls, notebook-scoped execution, path hardening, internal service topology. |
| Provider-Routed AI Services | Route each AI capability to the right local or cloud backend. | Chat, embeddings, image generation, ASR, TTS, document intelligence, local model lifecycle. |

## 4. AI product creation as the headline capability

Value statement:

GuideAnts lets teams create AI products, not just AI conversations. A guide can start as an internal workflow, become a reusable team capability, then be shared directly or embedded inside another application with controls around access, cost, retention, and observability.

Important nuance:

Published guides are both sharing and product embedding. A public guide URL is a sharing surface. The `guideants` web component and published guide APIs are product integration surfaces. These should be described as one continuum, not competing interpretations.

Proof features:

- Published guide configuration with general, interface, feature, limit, and auth tabs.
- Friendly-name public URLs.
- Published guide page that renders a guide using the `guideants-chat` web component.
- `guideants` npm package as an embeddable client surface.
- Public guide lookup by ID or friendly name.
- One-shot public guide invocation endpoint.
- Published notebook conversation endpoints for external chat, files, messages, and tool-call results.
- Interface controls such as display mode, command mode, turn navigation, collapsible mode, conversation starters, and attachments.
- Auth patterns using API key or webhook validation.
- Limits for message length, turn count, retention period, daily charge, and billing period charge.
- Guide and assistant usage analytics after publication.

Evidence anchors:

- `src/client/src/components/guides/PublishGuideDialog.tsx`
- `src/client/src/components/guides/configTabs/GeneralTab.tsx`
- `src/client/src/components/guides/configTabs/InterfaceTab.tsx`
- `src/client/src/components/guides/configTabs/FeaturesTab.tsx`
- `src/client/src/components/guides/configTabs/LimitsTab.tsx`
- `src/client/src/components/guides/configTabs/AuthTab.tsx`
- `src/client/src/pages/PublicGuide.tsx`
- `src/server/GuideAntsApi/Endpoints/GuidesPublishingEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/PublishedGuidesEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/PublishedNotebookConversationsEndpoints.cs`
- `src/server/GuideAntsApi.DataModel/Models/PublishedGuide.cs`
- External reference: `https://www.elumenotion.com/demos/powerbi`
- External reference: `https://www.npmjs.com/package/guideants`

## 5. Enterprise layer around created products

The enterprise capabilities are not separate from product creation. They are what make externally delivered AI experiences credible.

| Enterprise concern | Product creation value | Proof features |
|---|---|---|
| Content provenance | The AI product can be traced back to source files, notebook artifacts, and generated outputs. | Content versions, notebook origins, lineage events, markdown shadows. |
| Runtime safety | Embedded or external use can call powerful tools without exposing broad filesystem/runtime access. | Notebook-scoped sandbox execution, token-gated agent, path canonicalization, reparse-point rejection. |
| Cost risk | Public or embedded usage can be measured and constrained. | Usage events, charge fields, daily and billing-period limits. |
| Operational tuning | Teams can investigate model routing, service readiness, and runtime failures. | Telemetry presets, infrastructure probes, readiness endpoints, local runtime status. |
| Access control | Published experiences can be anonymous, key-protected, or webhook-validated depending on use case. | Friendly names, API keys, webhook auth, active/inactive publish state. |
| Lifecycle management | External experiences can be retired, limited, or cleaned up over time. | Deactivate/reactivate, retention period, retention cleanup jobs. |

## 6. Competitive framing

Avoid leading with:

- "Another self-hosted chat UI."
- "Supports multiple providers."
- "Has agents, files, and tools."
- "Embeddable chat widget."

Those claims are either table stakes or too small.

Stronger framing:

- "Create governed AI products from internal workflows."
- "Build once as a guide, then share, embed, integrate, and operate it."
- "Turn organizational expertise into reusable AI experiences with provenance, cost controls, and observability."
- "Bring AI into reports, portals, and domain apps without rebuilding the workflow engine."
- "Run AI work across local and cloud services with secure, modular execution boundaries."

## 7. Candidate website feature list

This is the shape I would move toward for a concise public feature list:

| Feature | Value copy | Proof points |
|---|---|---|
| Governed AI Workspaces | Organize AI work around projects, notebooks, source files, generated artifacts, and reusable context. | Files, links, notebooks, conversations, versioning, lineage, markdown extraction. |
| Guides and Assistants | Package repeatable expertise into reusable AI workflows that include instructions, tools, files, and model behavior. | Guide builder, assistant builder, crew, OpenAPI tools, context options, imports/exports. |
| AI Product Publishing | Share or embed guides as controlled AI experiences in other sites, reports, portals, and applications. | Friendly URLs, `guideants` web component, public APIs, interface controls, attachments. |
| Enterprise Controls | Govern published and internal AI experiences with access, limits, retention, and cost attribution. | API key/webhook auth, max turns, message limits, charge limits, usage analytics, retention cleanup. |
| Content Lineage | Track where AI-assisted work came from and how files move between project and notebook contexts. | File versions, origin tracking, publish-back, lineage events, markdown shadows. |
| Operable AI Infrastructure | Tune, inspect, and troubleshoot the AI system in production-like environments. | Telemetry settings, readiness checks, infrastructure probes, runtime inventory, load/unload flows. |
| Secure Modular Runtime | Run tools and local AI capabilities through scoped services rather than exposing broad system access. | `guideants-ai`, sandbox agent, notebook-scoped execution, path guards, internal service routing. |
| Provider-Routed Multimodal AI | Use the right backend for each capability instead of forcing one model or provider to do everything. | Chat, embeddings, document intelligence, image generation, speech transcription, speech synthesis. |

## 8. Recommendation

The next final-facing feature draft should lead with product creation:

> Build reusable AI workflows in GuideAnts, then share, embed, integrate, and operate them as governed AI products.

The enterprise story should immediately follow, because it explains why this is more than a demo widget: content lineage, cost control, observability, auth, retention, and secure modular execution are the difference between a neat chat integration and a product an organization can trust.

