# Voice pack & attribution — baked reference-voice pack for native (audio.cpp) TTS

Parent: [`00-overview.md`](./00-overview.md). Decisions: [`DECISIONS.md`](./DECISIONS.md)
**D10** (voice-pack sourcing + attribution compliance, this doc) + **D1** (TTS =
`chatterbox`, voice-clone only) + **D5** (`ga-audio-server` adapter) + **D9**
([`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md), which owns the
*model* download path — the voice pack is deliberately **outside** it). Gates:
[`contract-preservation-gate.md`](./contract-preservation-gate.md),
[`flavor-build-gate.md`](./flavor-build-gate.md).

**Mission:** voice-clone TTS families have **no built-in voices** — each synthesis needs a
reference WAV. This doc designs the **GuideAnts-owned, model-agnostic reference-voice pack**:
a curated set of openly-licensed short clips baked into the `guideants-ai` image, exposed as
a selectable-voice enum that **replaces the Kokoro `VoiceName` list**, with **full OSS
license compliance and attribution** and a **build-time completeness check** that fails
loudly when any non-CC0 clip is missing its attribution (honouring the repo "no silent
fallback / no silent gaps" rule).

This is a design/plan document. It changes no source, Docker, or config files; it wires a
new decision (**D10**) and cross-links the existing plan. Every claim is grounded in a repo
or audio.cpp path; specific speaker/utterance ids are marked **EXAMPLE / placeholder** and
are finalized during Phase 3 curation — none are presented as final.

---

## 1. Scope / non-goals

**In scope**

- A **model-agnostic, per-language pack of reference clips** (short clean WAVs) for
  **voice-clone TTS** families, with a versioned manifest, licence metadata, and attribution.
- **License compliance**: a NOTICE/ATTRIBUTIONS artifact baked into the image, the per-licence
  obligations, how attributions are surfaced, and a **build-time completeness gate**.
- **Replacing** the Kokoro voice enum (`ServiceEditorMetadataProvider.cs` `KokoroVoiceNames`,
  `:14-70`) with the pack voice ids. **No migration of legacy `VoiceName` values** — per
  product decision there is **no backwards compatibility**; stored Kokoro ids are not mapped.
- The **voice-picker UX** (constrained dropdown sourced from the pack manifest).

**Non-goals (boundaries only — designed elsewhere or out of this migration)**

- **Model downloads / the curated catalog + source allowlist** — that is **D9**
  ([`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md)). The voice pack is
  **not** an HF model download and **not** a catalog entry; it is local bundled assets
  (D9 §3.4 lines 141-159, §5.5 lines 314-327). The two flows must stay separate in the UI.
- **Embeddings** (llama.cpp GGUF, Phase 1), **llama chat** router presets, **Stable
  Diffusion** bundles — unaffected.
- **The TTS engine adapter itself** (`ga-audio-server` TTS task) — designed in
  [`phase-3-tts-decision.md`](./phase-3-tts-decision.md) §6; this doc only defines the
  **asset + manifest + licence** contract the adapter consumes.
- **Persona / likeness clearance** beyond copyright licence (see §8) — CC0 is safest;
  final legal sign-off is a Phase-3 curation task, marked *to validate in Phase 3*.

---

## 2. Which families need clips (model-agnostic, one consumer today)

audio.cpp's own TTS docs confirm that **most released TTS families are reference/clone-driven**,
so the pack is designed once and reused, not tied to Chatterbox:

| Family (audio.cpp) | Reference WAV | Evidence (`d:\repos\audio.cpp\docs\tts.md`) |
|---|---|---|
| `chatterbox` | **required** (`--voice-ref`) | `tts.md:34-35, 38, 43` — "Built-in voices: Not exposed" |
| `pocket_tts` | built-in **or** `--voice-ref` | `tts.md:188, 200-201, 207` |
| `miotts` | **required** (`--voice-ref`) | `tts.md:88, 92, 97` |
| `qwen3_tts` (clone / design) | reference audio or design prompt | `docs/qwen3.md`; catalog D9 §3.3 lines 119-122 |
| `voxcpm2` | optional `--voice-ref` (clone/ultimate-clone) | `tts.md:221, 233, 239, 245` |
| `omnivoice` | `--voice-ref` (clone) or `--instruct` (design) | `tts.md:148, 154, 167` |
| `vevo2` | reference-driven | `docs` / catalog D9 §3.3 line 128 |

Preset-voice-only families (`kokoro_tts` — **not in the release tree**; `supertonic` —
`integration`) do **not** need the pack; they are not the active TTS path (D1). `vibevoice`
takes **up to four** speaker refs (`tts.md:370, 375`) — the pack can supply them but multi-
speaker orchestration is out of scope here.

> **Design consequence:** clips are stored and described **independently of any one family**
> (a WAV + provenance + licence), and the adapter passes `clipPath` to whichever family is
> active via that family's reference flag (`--voice-ref` for Chatterbox). **Chatterbox is the
> only active consumer today (D1).** Adding another clone family later reuses the same pack —
> no re-licensing, no re-curation.

---

## 3. Voice-pack manifest schema

The pack ships as **image-layer assets** (§5.3): a directory of clips + a `manifest.json` +
a NOTICE file. The manifest is the single source of truth the adapter resolves `voice` →
clip against, and the build-time attribution check reads.

### 3.1 Per-voice fields

| Field | Type / values | Required | Meaning |
|---|---|---|---|
| `voiceId` | string, `^[a-z]{2}-[fmn]-[a-z0-9-]{1,32}$` (proposed) | yes | Stable selectable id; the value stored in `VoiceName` and sent as `voice`. |
| `displayName` | string | yes | Human label in the picker (e.g. "English (US) — Female 1"). |
| `language` | ISO-639-1 (`en`,`es`,`fr`,`hi`,`it`,`pt`, …) | yes | The pack language; maps to the engine `lang_code` (§6, §7). |
| `gender` | `female` \| `male` \| `neutral` | optional | Display/curation aid only. |
| `accent` | string (e.g. `en-US`, `en-GB`) | optional | Display/curation aid; disambiguates `a`/`b` English. |
| `sourceDataset` | enum (`common_voice` \| `globe` \| `vctk` \| `libritts_r`) | yes | Which corpus the clip came from. |
| `sourceClipRef` | string (speaker id + utterance/clip id) | yes | Exact provenance inside the dataset (traceable back to the source). |
| `licence` | `CC0-1.0` \| `CC-BY-4.0` | yes | SPDX-style licence id of the source clip. |
| `attribution` | object (see below) | required **iff** `licence != CC0-1.0` | Everything CC-BY-4.0 obliges. |
| `modified` | object `{ changed: bool, changes: string[] }` | yes | Declares trim/resample/normalize etc. — **CC-BY requires indicating modification.** |
| `clipPath` | string (relative to pack root, e.g. `clips/en-f-01.wav`) | yes | The WAV the adapter passes as the reference. |
| `sampleRate` | integer Hz (e.g. `24000`) | yes | Post-normalization rate (align to engine expectation — *to validate in Phase 3*). |
| `durationSec` | number (~5–10) | yes | Clip length; zero-shot clone quality target 5–10 s (§8). |
| `checksumSha256` | hex string | recommended | Integrity + reproducibility of the baked asset. |

`attribution` object (mandatory for CC-BY-4.0, recorded even for CC0 as provenance):

```jsonc
{
  "title": "<work / utterance title if any>",
  "creator": "<speaker / author or dataset-provided credit>",
  "sourceUrl": "<canonical dataset / clip URL>",
  "licenceUrl": "https://creativecommons.org/licenses/by/4.0/",
  "modificationNote": "trimmed to 8s; resampled to 24kHz; peak-normalized; converted to mono"
}
```

### 3.2 Manifest envelope (EXAMPLE starter set — placeholders, finalized in Phase 3)

The `sourceClipRef` values below are **illustrative placeholders**, not curated final ids.
They exist to show the schema shape and the CC0-first strategy; Phase 3 curation (per
[`phase-3-tts-decision.md`](./phase-3-tts-decision.md) §9.1) picks the real clips after a
listening review.

```jsonc
{
  "schemaVersion": 1,
  "packVersion": "0.1.0-draft",
  "generatedFrom": "<curation run ref>",
  "voices": [
    {
      "voiceId": "en-f-01",
      "displayName": "English (US) — Female 1",
      "language": "en", "gender": "female", "accent": "en-US",
      "sourceDataset": "common_voice",
      "sourceClipRef": "PLACEHOLDER:cv-en/client_id=<tbd>/clip=<tbd>.mp3",
      "licence": "CC0-1.0",
      "attribution": null,
      "modified": { "changed": true, "changes": ["trim", "resample_24k", "mono", "normalize"] },
      "clipPath": "clips/en-f-01.wav", "sampleRate": 24000, "durationSec": 8.0,
      "checksumSha256": "<tbd>"
    },
    {
      "voiceId": "en-m-gb-01",
      "displayName": "English (GB) — Male 1",
      "language": "en", "gender": "male", "accent": "en-GB",
      "sourceDataset": "vctk",
      "sourceClipRef": "PLACEHOLDER:VCTK/speaker=pXXX/utt=XXX",
      "licence": "CC-BY-4.0",
      "attribution": {
        "title": "VCTK Corpus (0.92) — speaker pXXX",
        "creator": "CSTR, University of Edinburgh (and the speaker)",
        "sourceUrl": "https://datashare.ed.ac.uk/handle/10283/3443",
        "licenceUrl": "https://creativecommons.org/licenses/by/4.0/",
        "modificationNote": "trimmed to 7s; resampled to 24kHz; mono; normalized"
      },
      "modified": { "changed": true, "changes": ["trim", "resample_24k", "mono", "normalize"] },
      "clipPath": "clips/en-m-gb-01.wav", "sampleRate": 24000, "durationSec": 7.0,
      "checksumSha256": "<tbd>"
    }
    // … es-f-01, fr-f-01, hi-f-01, it-f-01, pt-f-01 … (see §4 EXAMPLE table)
  ]
}
```

---

## 4. Sourcing strategy per language (CC0-first)

**Rule of thumb:** prefer **CC0** (no attribution burden, safest persona footing); use
**CC-BY-4.0** only where CC0 coverage/quality is insufficient, and then **attribution is
mandatory** (§5) and enforced by the build check.

| Dataset | Licence (re-stated; **re-verify at curation**) | Attribution needed? | Notes |
|---|---|---|---|
| Mozilla **Common Voice** | **CC0-1.0** (public domain dedication) | No (record provenance anyway) | Broadest language coverage incl. es/fr/hi/it/pt; variable quality → curate. |
| **GLOBE** (GLOBE_V2, derived from Common Voice) | **CC0-1.0** | No | High-quality **English** zero-shot corpus; en only. |
| **VCTK** (0.92) | **CC-BY-4.0** | **Yes** | Clean studio **English** (US/GB accents); good when CC0 en quality falls short. |
| **LibriTTS-R** | **CC-BY-4.0** | **Yes** | Restored LibriTTS; clean **English** reading voices. |

> **CC vs likeness:** Creative Commons licences cover **copyright**, not personality/likeness
> or persona rights. CC0 consented-synthesis-friendly corpora (Common Voice / GLOBE) are the
> safest choice for a shipped product voice. Any use of a recognizable individual's voice is
> a Phase-3 legal review item — *to validate in Phase 3*.

**Coverage note (honest gap):** CC0 non-English clean speech (es/fr/hi/it/pt) exists in
Common Voice but is more variable than studio English; VCTK/LibriTTS-R are **English-only**.
So for **es/fr/hi/it/pt** the practical CC0 source is **Common Voice** (curated), and the
CC-BY fallback datasets do **not** help non-English. If a language cannot reach quality on
CC0, that is a **documented coverage decision** (ship fewer voices for that language, or
accept a CC-BY source only if one exists for it) — never a silent substitution with a
wrong-language or lower-quality clip.

### 4.1 EXAMPLE starter set (placeholders — finalized in Phase 3)

Covers the D1-required languages `en, es, fr, hi, it, pt` (Chatterbox has all six —
`tts.md:33`). **These are examples of the shape and the CC0-first intent, not final ids.**

| `voiceId` (example) | language | gender | example source (placeholder) | licence |
|---|---|---|---|---|
| `en-f-01` | en (US) | female | Common Voice en (CC0) | CC0-1.0 |
| `en-m-01` | en (US) | male | Common Voice en (CC0) | CC0-1.0 |
| `en-f-gb-01` | en (GB) | female | GLOBE / Common Voice en (CC0) | CC0-1.0 |
| `en-m-gb-01` | en (GB) | male | **VCTK pXXX** (CC-BY, if CC0 GB quality insufficient) | CC-BY-4.0 |
| `es-f-01` | es | female | Common Voice es (CC0) | CC0-1.0 |
| `fr-f-01` | fr | female | Common Voice fr (CC0) | CC0-1.0 |
| `hi-f-01` | hi | female | Common Voice hi (CC0) | CC0-1.0 |
| `it-f-01` | it | female | Common Voice it (CC0) | CC0-1.0 |
| `pt-f-01` | pt | female | Common Voice pt (CC0) | CC0-1.0 |

---

## 5. License compliance mechanism

### 5.1 What each licence obligates

- **CC0-1.0:** **no** obligation to attribute. GuideAnts still **records provenance**
  (`sourceDataset` + `sourceClipRef`) in the manifest for traceability and reproducibility —
  provenance is recorded even when not legally required.
- **CC-BY-4.0:** must provide, in a reasonable manner: **title** (if supplied), **creator**,
  **source** (link), the **licence** name + link, and an **indication if modifications were
  made** (all clips are trimmed/resampled → `modified.changed = true` and a
  `modificationNote`). All five live in the manifest `attribution` object and are copied into
  the NOTICE file.

### 5.2 The NOTICE / ATTRIBUTIONS artifact (baked into the image)

Proposed path (image layer, alongside the clips):

```
/opt/guideants/voice-pack/
├── manifest.json
├── NOTICE.md              ← human-readable attributions (all CC-BY entries + CC0 provenance)
└── clips/
    ├── en-f-01.wav
    ├── en-m-gb-01.wav
    └── …
```

`NOTICE.md` is generated from `manifest.json` at build time (single source of truth → no
drift). It lists, per non-CC0 clip: title, creator, source URL, licence + URL, and the
modification note; and, per CC0 clip, a provenance line (dataset + clip ref) even though not
required. The same content is mirrored into this docs folder as a released artifact so the
attributions are discoverable outside the running image.

### 5.3 Where the clips live — image layer vs volume (tradeoff)

| Option | Pro | Con |
|---|---|---|
| **Image layer (baked, recommended)** — `COPY voice-pack/ /opt/guideants/voice-pack/` in `docker/build/guideants-ai/Dockerfile.*`, mirroring how service dirs and seeds are copied today (`Dockerfile.cuda:197-217`, e.g. `COPY router-models.seed.ini …`) | Versioned **with the image tag**; immutable; rollback = image-tag rollback; NOTICE + clips + manifest can never drift apart; reproducible provenance | Grows the image (small: a few short WAVs); changing the pack needs a rebuild |
| Model/assets **volume** (mutable) | Editable without rebuild | **Not versioned with the image**; NOTICE can drift from the actual clips; violates the compliance-provenance goal; conflates with the `/models-local` **download** volume that D9 keeps clip-free |

**Recommendation: image layer.** Models are downloaded to the `/models-local/{asr,tts}`
volume (D9); the voice pack is **shipped code/assets**, so it belongs in the image (matches
D1/D9 framing — "baked into the image", D9 §3.4 line 149). *This doc references the COPY
pattern; it does not edit the Dockerfile — that is a Phase-3 change.*

### 5.4 Build-time attribution-completeness check (loud failure, no silent gaps)

A build/CI step validates `manifest.json` **before** the image is accepted:

1. Every `voiceId` matches the id pattern and is unique.
2. Every entry has a `clipPath` that **exists** in `clips/` (and, if `checksumSha256` is
   present, matches).
3. **Every `licence != CC0-1.0` entry has a complete `attribution`** (all of: `creator`,
   `sourceUrl`, `licenceUrl`; `title` if the dataset supplies one) **and** `modified.changed`
   is set with a non-empty `modificationNote` when any change was made.
4. `sourceDataset` ∈ the allowed set; `licence` ∈ {`CC0-1.0`, `CC-BY-4.0`}.
5. `NOTICE.md` regenerated from the manifest matches the committed `NOTICE.md` (no drift).

**Any failure fails the build/gate loudly** — the offending clip is **never** silently
dropped or shipped without attribution (repo "no silent fallback" rule). This check is folded
into the [`contract-preservation-gate.md`](./contract-preservation-gate.md) as a Phase-3
assertion (§9), keeping the gate count stable rather than adding a new gate doc.

---

## 6. Voice selection — no legacy migration / no backwards compatibility

**Product decision:** legacy Kokoro `VoiceName` values are **not** migrated, mapped, or
preserved. The pack `voiceId`s **replace** `KokoroVoiceNames` outright; there is no forward
map, no reverse map, and no data migration over stored service modes.

### 6.1 How `VoiceName` is stored today (verified)

- The enum options come from `ServiceEditorMetadataProvider.KokoroVoiceNames`
  (`ServiceEditorMetadataProvider.cs:14-70`), fed to the `VoiceName` field of
  `SpeechSynthesisLocalTtsHttp` (`:224-228`). The client renders it as a dropdown
  (`type: "enum"`, `enumOptions`) — **no client rendering change is needed**, only the option
  set.
- The **stored value** is persisted inside the service mode's `RequestPresetJson` as a JSON
  string property `VoiceName`, read by
  `SpeechSynthesisService.ResolveServiceModePresetString(mode, "VoiceName")`
  (`SpeechSynthesisService.cs:840-862`, called from `:817`). The seed default becomes a pack
  `voiceId` (e.g. `en-f-01`) in the bootstrap profile
  (`Resources/bootstrap/provider-stack-profiles/local-ai.json`).

### 6.2 Legacy stored values

An existing service mode that still holds a Kokoro id (e.g. `af_heart`) is **not** rewritten.
On use, a `voiceId` not present in the pack manifest is **rejected loudly** and the user
reselects from the new dropdown — there is **no silent remap and no fallback voice** (honours
the user's no-fallback rule). No migration log, no rollback record.

### 6.3 Language resolver fix (independent of migration — still required)

Today `lang_code` is **derived from the voice id's first character**
(`ResolveLocalKokoroLanguageCode`, `SpeechSynthesisService.cs:823-826` → `voiceName[0]`),
which only worked because Kokoro ids encode language in char 0.

> **Load-bearing nuance (a real plan delta, unrelated to any migration):** the new `voiceId`
> scheme (`en-f-01`, …) is **not** parseable by the `voiceName[0]` heuristic (`en-f-01`[0] =
> `e` would wrongly mean Spanish). Language must come from the **manifest** (`language` field),
> **not** char 0. The wire request shape to `/tts/synthesize`
> (`{ text, voice, lang_code, speed }`) is unchanged, so this internal resolver change is
> contract-neutral — but it must be called out in the Phase-3 PR (see §9 and
> `phase-3-tts-decision.md` §6). This is required simply because the voice ids changed, with
> or without any legacy migration.

---

## 7. UX

- The **voice picker** is a **constrained dropdown** whose options are the pack manifest's
  `voiceId`s (via `displayName`) — the same `enum` field mechanism already used
  (`ServiceEditorMetadataProvider.cs:227`), so no new client control. It is **separate from
  the D9 model picker**: model download (`chatterbox` from `ResembleAI/chatterbox`) lives in
  the model manager; voice selection lives in the service-editor provider settings
  (D9 §5.5 lines 314-327).
- Each option shows **language** (+ accent) and, ideally, a **licence/attribution affordance**
  (a "voice info / about" tooltip or link surfacing `sourceDataset` + licence, and the CC-BY
  attribution when applicable) — sourced from the same manifest that feeds the NOTICE.
- **Determinism note:** Chatterbox samples stochastically (`--do-sample true`, temperature
  0.8 — `tts.md:47, 51`). The adapter **pins a per-request seed keyed on (text, voiceId)** so a
  given (text, voice) is reproducible (D1; `phase-3-tts-decision.md:124-125, 168-169`). The
  picker/UX makes no promise of cross-voice determinism, only per-(text, voice) stability.

**Engine resolution flow (how the pack reaches Chatterbox):** .NET sends
`{ text, voice=<voiceId>, lang_code, speed }` to `/tts/synthesize`
(`SpeechSynthesisService.cs:685-695`, contract unchanged). `ga-audio-server` looks up
`voiceId` in `manifest.json` → `clipPath` → invokes Chatterbox `task clon` with
`--voice-ref /opt/guideants/voice-pack/clips/<file>.wav` and maps `lang_code`
(`a,b→en, e→es, f→fr, h→hi, i→it, p→pt`; **`j`/`z` rejected loudly**). **If `voiceId` is not
in the manifest → reject loudly** (no fallback to a default clip). (Adapter design:
`phase-3-tts-decision.md` §6.)

---

## 8. Risks

- **Likeness / persona rights.** CC covers copyright, not likeness. Mitigation: **CC0-first**
  (Common Voice / GLOBE); recognizable-individual voices are a Phase-3 legal review — *to
  validate in Phase 3*.
- **Curation quality (zero-shot clone).** Poor reference clips → poor cloned output.
  Mitigation: listening review over ≥20 sentences/language on Chatterbox
  (`phase-3-tts-decision.md:180-183, 220-221`) before locking the pack.
- **Per-language coverage gaps.** CC0 non-English (es/fr/hi/it/pt) quality is variable and
  VCTK/LibriTTS-R are English-only (§4). If a language can't hit quality on CC0, **document
  the reduced voice count** — never substitute a wrong-language/low-quality clip silently.
- **Attribution completeness.** A CC-BY clip shipped without full attribution is a licence
  violation. Mitigation: the **build-time gate** (§5.4) fails loudly — no silent gap.
- **Clip length for zero-shot (~5–10 s).** Too short → unstable timbre; too long → wasted
  conditioning. `durationSec` target 5–10 s, validated in curation.
- **`voiceId` language derivation regression.** The old char-0 heuristic breaks on the new ids
  (§6.1); if the resolver isn't updated, `lang_code` would be wrong. Mitigation: manifest-driven
  language resolution, tested in the Phase-3 contract check.
- **Sample-rate mismatch.** The reference WAV rate the Chatterbox port expects is *to validate
  in Phase 3*; `sampleRate` is recorded per clip and clips are normalized to a single rate.

---

## 9. Validation / DoD / Gates / Rollback

**Definition of Done**

- [ ] `manifest.json` exists with the §3 schema; every voice has `voiceId`, `language`,
      `sourceDataset`, `sourceClipRef`, `licence`, `clipPath`, `sampleRate`, `durationSec`.
- [ ] Every **CC-BY-4.0** clip has complete `attribution` + `modified` note; **CC0** clips
      record provenance. `NOTICE.md` generated from the manifest, mirrored to docs.
- [ ] The **build-time attribution-completeness check passes** (green) and is proven to
      **fail loudly** on a synthetic missing-attribution entry (§5.4).
- [ ] Voice picker lists **only** pack `voiceId`s; language + licence surfaced; picker is
      separate from the D9 model picker.
- [ ] `KokoroVoiceNames` replaced by pack ids in `ServiceEditorMetadataProvider.cs`
      (the D1-approved contract change); resolver derives `lang_code` from the manifest, not
      char 0.
- [ ] **No migration code**: legacy `VoiceName` values are not mapped; an unknown/legacy voice
      id is rejected loudly (no silent remap, no fallback voice) and requires reselection.
- [ ] Per-(text, voice) determinism via pinned seed verified.

**Gates**

- [`contract-preservation-gate.md`](./contract-preservation-gate.md): `/tts/synthesize` shape
  unchanged; the **only** approved change is the voice-preset list replacement (already the
  gate's Phase-3 approved delta, `:76`). This doc **adds the voice-pack
  attribution-completeness assertion** to that gate's Phase-3 row (§5.4) — no CC-BY clip ships
  without attribution.
- [`flavor-build-gate.md`](./flavor-build-gate.md): the pack is flavor-agnostic (identical
  clips baked into every flavor); the added COPY layer builds on all four flavors.
- [`torch-removal-gate.md`](./torch-removal-gate.md): unaffected (assets only, no packages).

**Rollback**

- The pack + manifest + NOTICE are **shipped image assets** → rollback is **image-tag
  rollback**, fully stateless. There is **no `VoiceName` data migration** to reverse (§6); a
  rollback to the Kokoro image simply restores the old enum, and any service mode saved with a
  new pack `voiceId` in the interim is rejected loudly on the old image (user reselects) —
  consistent with the no-backwards-compat decision.

---

## 10. Cross-links

- **D1** — [`phase-3-tts-decision.md`](./phase-3-tts-decision.md) §5.1 (voice-clone only, no
  built-in voices; open voice sources; determinism; `j`/`z` drop) and §6 (adapter + voice-pack
  asset row). This doc is the authority those bullets point to.
- **D9** — [`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md) §3.4 / §5.5:
  the voice pack is **local assets, out of the model-download/allowlist path**.
- **D10** — [`DECISIONS.md`](./DECISIONS.md): voice-pack sourcing + attribution compliance
  (this doc's decision), and [`STATUS.md`](./STATUS.md) ledger entry.
