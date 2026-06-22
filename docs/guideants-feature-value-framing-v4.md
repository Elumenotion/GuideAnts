# GuideAnts Feature Value Framing (Draft v4)

Status: Draft for discussion
Date: 2026-06-08
Builds on: `docs/guideants-feature-value-framing-v3.md`

## 1. Core positioning

Recommended positioning:

> GuideAnts is a platform for creating governed AI work products: teams build reusable guides inside structured workspaces, tune them with traceable cost and behavior data, then share, embed, integrate, and operate them as controlled AI experiences.

The key addition in this revision is **Tune**. It should be treated as a primary value proposition, not an operational afterthought.

GuideAnts is strongest when the story includes:

- Product creation: guides become shareable and embeddable AI products.
- Traceable tuning: teams can see what happened, what it cost, and what can be optimized.
- Enterprise governance: published experiences can be controlled, observed, limited, and safely operated.

## 2. Product creation and tuning lifecycle

GuideAnts product creation should be described as an iterative lifecycle:

| Stage | What the user is doing | GuideAnts value |
|---|---|---|
| Build | Create a guide or assistant from instructions, files, tools, model choices, and workflow design. | Expertise becomes a reusable asset instead of a one-off prompt. |
| Validate | Test the guide in notebooks, conversations, and internal workflows. | Teams can check behavior before exposing the experience to others. |
| Tune | Use traceability, usage attribution, cost data, model routing, and observed outputs to improve quality/cost. | Teams can move from expensive prototype behavior to sustainable production behavior. |
| Ground | Attach the tuned experience to durable project/notebook content, files, markdown extraction, context options, and generated artifacts. | The AI product has reliable source material and persistent work context. |
| Share | Publish with a friendly URL or controlled access pattern. | The workflow becomes available beyond the authoring environment. |
| Embed | Use the `guideants` web component to place a published guide inside another application. | The AI experience becomes part of a domain-specific product surface. |
| Integrate | Use public guide APIs, client context, attachments, and published conversation flows. | Existing apps and reports can call into the guide rather than redirect users elsewhere. |
| Govern | Apply auth, limits, retention, usage analytics, observability, and runtime controls. | The published AI product remains operable and accountable. |

This loop is not strictly linear. Mature teams will cycle through **Validate -> Tune -> Ground** many times as they improve prompts, assistants, models, routing, tools, and context.

## 3. Traceable tuning as a core value proposition

Value statement:

GuideAnts helps teams tune AI workflows from impressive prototypes into economically sustainable products. Because usage is attributed to projects, notebooks, conversations, assistants, invocations, messages, services, operations, models, and charges, teams can identify where spend is coming from and optimize without losing the product workflow.

Why this matters:

AI product failures are increasingly cost failures, not just quality failures. Frontier models can make an early version look great, but without traceability and controls the same workflow may be too expensive to run. GuideAnts makes tuning visible: teams can test with stronger models, inspect the trace and cost profile, then move suitable parts of the workflow to smaller or cheaper models.

Illustrative operating story:

EveryEventEver is maintained using published guides. The first version used a state-of-the-art frontier model and cost hundreds of dollars per day to operate. By using the traceability and tuning loop, the workflow was moved to mini models where appropriate, bringing similar inference down to about ten dollars per day.

Proof features:

- Usage events capture category, service, operation, model deployment, token/value metrics, cost, markup, and final charge.
- Usage can be attributed to project, notebook, conversation, assistant, invoking assistant, agent invocation, and individual conversation message.
- Guide/assistant usage surfaces expose summaries, charts, crew activity, conversations, invocations, and turn messages.
- Runtime profiles and model catalog entries let teams tune model behavior and provider choices.
- Chat defaults and per-model/runtime settings support cost/quality tradeoffs.
- Provider-routed services allow one workflow to mix strong models where needed and smaller/local models where sufficient.
- Published guide charge limits act as a guardrail once the workflow is externalized.

Evidence anchors:

- `src/server/GuideAntsApi.DataModel/Models/UsageEvent.cs`
- `src/server/GuideAnts.Usage/EfUsageRecorder.cs`
- `src/server/GuideAntsApi/Endpoints/GuideUsageEndpoints.cs`
- `src/client/src/pages/GuideUsagePage.tsx`
- `src/client/src/components/usage/InvocationTree.tsx`
- `src/client/src/components/usage/InvocationDetailPanel.tsx`
- `src/client/src/components/usage/TurnMessagesPanel.tsx`
- `src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs`
- `src/client/src/pages/settings/components/ModelsRuntimeWorkspace.tsx`
- `src/client/src/pages/settings/components/ProfilesTab.tsx`
- `src/client/src/components/chat-model/ChatModelConfigurator.tsx`
- `src/server/GuideAntsApi/Services/PublishedGuides/PublishedGuideCostLimitService.cs`

## 4. Revised value pillars

| Pillar | Value promise | What to emphasize |
|---|---|---|
| Governed AI Workspaces | Keep AI work organized, contextual, and durable. | Projects, notebooks, files, links, conversations, artifacts, home pages, versioning, lineage. |
| Reusable Expertise | Convert organizational know-how into reusable guide and assistant assets. | Instructions, tools, files, context options, crew patterns, conversation starters, imports/exports. |
| Traceable Tuning | Improve quality and cost using usage attribution, traces, runtime profiles, and model routing. | Usage events, invocation trees, message attribution, model costs, runtime profiles, chat defaults. |
| AI Product Creation | Turn internal guides into shareable, embeddable, and integrated AI experiences. | Published guides, friendly URLs, `guideants` web component, public guide APIs, domain app/report integration. |
| Enterprise Governance | Preserve provenance, manage lifecycle, and support audit/compliance needs. | File versioning, lineage events, markdown shadows, notebook sync, retention cleanup, role controls. |
| Cost and Usage Control | Attribute and limit AI activity before it becomes operational or financial risk. | Charge tracking, guide analytics, daily limits, billing-period limits, message/turn limits. |
| Observability and Operations | Give operators tools to tune, debug, and prove what happened. | Telemetry presets, structured categories, infrastructure probes, readiness checks, runtime status. |
| Secure Modular Runtime | Isolate risky or heavyweight capabilities behind scoped internal services. | Sandbox agent, token-gated API-to-agent calls, notebook-scoped execution, path hardening, internal service topology. |
| Provider-Routed AI Services | Route each AI capability to the right local or cloud backend. | Chat, embeddings, image generation, ASR, TTS, document intelligence, local model lifecycle. |

## 5. AI product creation as the headline capability

Value statement:

GuideAnts lets teams create AI products, not just AI conversations. A guide can start as an internal workflow, become a reusable team capability, be tuned into an economically sustainable product, and then be shared directly or embedded inside another application with controls around access, cost, retention, and observability.

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
- External reference: `https://everyeventever.com/`

## 6. Enterprise layer around created products

The enterprise capabilities are not separate from product creation. They are what make externally delivered AI experiences credible and affordable.

| Enterprise concern | Product creation value | Proof features |
|---|---|---|
| Content provenance | The AI product can be traced back to source files, notebook artifacts, and generated outputs. | Content versions, notebook origins, lineage events, markdown shadows. |
| Tuning traceability | Teams can see which model, assistant, message, operation, or tool invocation drove cost or behavior. | Usage attribution, invocation trees, turn messages, model deployment IDs, metadata. |
| Runtime safety | Embedded or external use can call powerful tools without exposing broad filesystem/runtime access. | Notebook-scoped sandbox execution, token-gated agent, path canonicalization, reparse-point rejection. |
| Cost risk | Public or embedded usage can be measured, optimized, and constrained. | Usage events, charge fields, runtime profiles, provider routing, daily and billing-period limits. |
| Operational tuning | Teams can investigate model routing, service readiness, and runtime failures. | Telemetry presets, infrastructure probes, readiness endpoints, local runtime status. |
| Access control | Published experiences can be anonymous, key-protected, or webhook-validated depending on use case. | Friendly names, API keys, webhook auth, active/inactive publish state. |
| Lifecycle management | External experiences can be retired, limited, or cleaned up over time. | Deactivate/reactivate, retention period, retention cleanup jobs. |

## 7. Competitive and market framing

Avoid leading with:

- "Another self-hosted chat UI."
- "Supports multiple providers."
- "Has agents, files, and tools."
- "Embeddable chat widget."

Those claims are either table stakes or too small.

Stronger framing:

- "Create governed AI products from internal workflows."
- "Validate, tune, ground, share, embed, integrate, and operate AI experiences."
- "Use traceability to move from frontier-model prototypes to sustainable production economics."
- "Turn organizational expertise into reusable AI experiences with provenance, cost controls, and observability."
- "Bring AI into reports, portals, and domain apps without rebuilding the workflow engine."
- "Run AI work across local and cloud services with secure, modular execution boundaries."

Market context:

Recent AI-budget stories make this positioning timely. The lesson is not "avoid expensive models." The lesson is "use expensive models deliberately, measure where they help, and tune the workflow so production economics make sense."

Claude Opus 4.8 and similar frontier models can be excellent for validation, difficult reasoning, coding, or complex agentic work. GuideAnts should position itself as the platform that helps teams decide where those models are worth it, where mini/local models are enough, and how to keep the resulting product controlled.

## 8. Candidate website feature list

This is the shape I would move toward for a concise public feature list:

| Feature | Value copy | Proof points |
|---|---|---|
| Governed AI Workspaces | Organize AI work around projects, notebooks, source files, generated artifacts, and reusable context. | Files, links, notebooks, conversations, versioning, lineage, markdown extraction. |
| Guides and Assistants | Package repeatable expertise into reusable AI workflows that include instructions, tools, files, and model behavior. | Guide builder, assistant builder, crew, OpenAPI tools, context options, imports/exports. |
| Traceable Tuning | See what happened, what it cost, and which model or assistant caused it, then tune for sustainable production use. | Usage attribution, invocation trees, model IDs, runtime profiles, charge tracking. |
| AI Product Publishing | Share or embed guides as controlled AI experiences in other sites, reports, portals, and applications. | Friendly URLs, `guideants` web component, public APIs, interface controls, attachments. |
| Enterprise Controls | Govern published and internal AI experiences with access, limits, retention, and cost attribution. | API key/webhook auth, max turns, message limits, charge limits, usage analytics, retention cleanup. |
| Content Lineage | Track where AI-assisted work came from and how files move between project and notebook contexts. | File versions, origin tracking, publish-back, lineage events, markdown shadows. |
| Operable AI Infrastructure | Tune, inspect, and troubleshoot the AI system in production-like environments. | Telemetry settings, readiness checks, infrastructure probes, runtime inventory, load/unload flows. |
| Secure Modular Runtime | Run tools and local AI capabilities through scoped services rather than exposing broad system access. | `guideants-ai`, sandbox agent, notebook-scoped execution, path guards, internal service routing. |
| Provider-Routed Multimodal AI | Use the right backend for each capability instead of forcing one model or provider to do everything. | Chat, embeddings, document intelligence, image generation, speech transcription, speech synthesis. |

## 9. Recommendation

The next final-facing feature draft should lead with product creation and traceable tuning:

> Build reusable AI workflows in GuideAnts, validate and tune them with traceable usage data, then share, embed, integrate, and operate them as governed AI products.

The tuning story is what makes the product creation story credible. It says GuideAnts can help teams get from "this works with a frontier model" to "this is sustainable enough to run every day."

