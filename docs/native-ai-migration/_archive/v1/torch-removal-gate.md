# Torch-Removal Gate — Native AI Migration

Companion to [`00-overview.md`](./00-overview.md). This gate proves the **explicit
torch-removal outcome** of the migration: after ASR, TTS, and embeddings have all left the
torch stack, the torch-dependent packages are actually gone from the service venv and the
image is smaller — or, where torch itself cannot yet be removed, that the reason is
recorded honestly rather than papered over.

Grounded in [`torch-dependencies-report.md`](./torch-dependencies-report.md) (the
authoritative audit) and verified against the live sources on 2026-07-02.

> **Read this first — the single-venv reality.** `guideants-ai` has **one** Python
> environment, `/opt/venv`. `Dockerfile.cuda:150-170` builds it and installs
> `torch/torchaudio/torchvision` (`:160`), then the ASR/TTS/emb requirements, then the
> **filtered sandbox** `requirements.txt` (`:166`, sourced from
> `Sandboxes/python311Torch<backend>/requirements.txt` with `torch*` stripped by
> `build_guideants_ai.sh:244-259`). The ScriptExecutionAgent's admin reconcile installs
> **into the same venv** (`script-agent-admin/reconcile.sh:146` →
> `/opt/venv/bin/python -m pip install`). So the "service venv" and the "sandbox
> execution venv" are **the same venv**. This entangles the two torch concerns — see §4
> and [`DECISIONS.md`](./DECISIONS.md) D7/D8. This gate distinguishes *what the ASR/TTS/emb
> migration can remove on its own* from *what full torch removal additionally requires*.

---

## 1. Two removal tiers (do not conflate them)

| Tier | What is removed | Enabled by | Blocks |
|------|-----------------|------------|--------|
| **Tier A — torch-dependent SERVICE packages** | `sentence-transformers` (Phase 1), `qwen-asr` + the ASR use of `accelerate` (Phase 2), `kokoro` + `misaki` + `curated-transformers` + `spacy-curated-transformers` (Phase 3), and `transformers`/`tokenizers`/`accelerate` **iff** nothing remaining requires them | Phases 1–3 (final removal wired in Phase 3/4) | Nothing further; invisible to sandbox users because none of these are declared by the sandbox requirements (verified) |
| **Tier B — torch itself** (`torch`, `torchaudio`, `torchvision`) | The multi-GB wheels | Tier A **plus** a sandbox-scope decision (D7) to stop offering torch to user scripts, because the sandbox declares `torch`/`torchaudio`/`torchtext` (`Sandboxes/python311TorchCUDA/requirements.txt:1-2,181`) and shares `/opt/venv` | The **image-size headline win**. Cannot be claimed by Phases 1–3 alone. |

**Consequence, stated plainly:** completing Phases 1–3 achieves **Tier A** and makes
`/opt/venv` free of every package that *depends on* torch. It does **not**, by itself,
make `pip show torch` fail, because torch is still declared by the sandbox requirement set
and installed into the shared venv. Claiming "PyTorch removed from the full image" is only
true after **Tier B**, which is gated on D7 (sandbox torch decision) and D8 (TTS resolved).

---

## 2. Gate intent

Run at final acceptance (and re-run whenever a phase removes a package tier). Pass when the
tier under test is provably clean and honestly scoped:

- **Tier A exit:** for each full flavor (cuda13/vulkan/rocm/cpu), the reverse-dependency
  tree of torch (`pipdeptree -r -p torch`) contains **no service package** — i.e. none of
  `sentence-transformers`, `qwen-asr`, `kokoro`, `misaki`, `curated-transformers`,
  `spacy-curated-transformers`, and (if Tier A also drops them) `transformers`,
  `tokenizers`, `accelerate`. Any remaining requirer is either a sandbox package (expected
  under Tier A) or a defect to investigate.
- **Tier B exit (only when D7 authorizes it):** `pip show torch` **fails** in `/opt/venv`
  on all full flavors; `torchaudio` and `torchvision` likewise; `pipdeptree -r -p torch`
  errors with "package not found"; the image builds with **no** `download.pytorch.org`
  index reference remaining in any `Dockerfile.*` and **no** `torch*` line in the copied
  sandbox `requirements.txt`.
- **Image-size delta recorded** per flavor (before/after), Tier A and Tier B separately —
  Tier A shrinks by the service packages; the large delta is Tier B only.
- **No silent fallback introduced** (user rule): removal must not be masked by a
  try/except-imported torch shim or a "if torch unavailable, degrade" branch. Where a
  removed capability is genuinely gone, the failure is explicit.

---

## 3. Procedure

1. **Baseline capture (pre-Phase-1):** on each full flavor record
   `pip show torch torchaudio torchvision`, the full `pipdeptree -r -p torch` tree, and
   `/opt/venv` size. Store in [`STATUS.md`](./STATUS.md). (Reproduction commands are in
   `torch-dependencies-report.md` §4.)
2. **After Phase 1 (emb):** `sentence-transformers` gone from the torch reverse-dep tree;
   emb still healthy on `/emb/embed`.
3. **After Phase 2 (asr):** `qwen-asr` gone; `accelerate`'s ASR requirer gone (accelerate
   may remain if TTS still uses it — verify against `tts-requirements.txt:1`).
4. **After Phase 3 (tts):** `kokoro`, `misaki`, `curated-transformers`,
   `spacy-curated-transformers`, and `accelerate` all gone. Now decide, per D7, whether to
   also delete `transformers`/`tokenizers` — only if `pipdeptree` shows **no** remaining
   requirer (including transitive sandbox packages). **Do not delete on assumption.**
5. **Tier B (only if D7 = remove sandbox torch):** delete the `torch* ` lines from
   `Sandboxes/python311Torch<backend>/requirements.txt`, remove the explicit torch install
   + `--index-url https://download.pytorch.org/whl/*` from every `Dockerfile.*`, rebuild,
   and assert `pip show torch` fails on all flavors.
6. **Record** the per-flavor before/after `/opt/venv` size and the reverse-dep tree state
   in [`STATUS.md`](./STATUS.md) and [`acceptance-evidence.md`](./acceptance-evidence.md).

---

## 4. Entry / exit criteria

- **Entry:** the phase(s) enabling the tier under test are merged; contract-preservation
  and flavor-build gates already green for those phases (torch removal must not regress a
  live contract). For Tier B, **D7 resolved** (sandbox torch) and **D8 satisfied** (TTS
  off torch, i.e. Phase 3 done — otherwise kokoro keeps torch and Tier B is impossible).
- **Exit — Tier A:** reverse-dep tree free of the service packages listed in §2 on all
  four full flavors; per-flavor size delta recorded; no silent-fallback shim.
- **Exit — Tier B:** `pip show torch|torchaudio|torchvision` all fail on all four full
  flavors; no `download.pytorch.org` index in any Dockerfile; no `torch*` in the copied
  sandbox requirements; large image-size delta recorded. If D7 keeps sandbox torch, Tier B
  is explicitly marked **NOT ATTEMPTED (sandbox decision)** — not silently skipped.

---

## 5. Reproduction commands (fill outputs at acceptance)

```bash
# per full flavor (cuda13/vulkan/rocm/cpu)
docker exec guideants-ai /opt/venv/bin/pip show torch torchaudio torchvision   # → <before/after>
docker exec guideants-ai /opt/venv/bin/pipdeptree -r -p torch                  # → <tree or "not found">
docker exec guideants-ai du -sh /opt/venv                                      # → <size before/after>
# Tier B build assertion
grep -RnE 'download\.pytorch\.org|^\s*torch(vision|audio)?==' docker/build/guideants-ai/Dockerfile.* \
    docker/build/guideants-ai/requirements.txt                                 # → <none, post-Tier-B>
```

---

## 6. Report-back addition (torch-removal gate)

```text
TORCH-REMOVAL GATE:
- Tier A (service packages) reverse-dep tree clean (cuda13/vulkan/rocm/cpu): <p/p/p/p>
- transformers/tokenizers/accelerate removed OR retained-with-requirer (which): <state>
- Tier B attempted? (needs D7): <yes/no + why>
- pip show torch fails all full flavors (Tier B only): <p/p/p/p or N/A>
- No download.pytorch.org index left; no torch* in sandbox reqs (Tier B only): <p/f or N/A>
- Image-size delta per flavor (Tier A / Tier B): <numbers — placeholders until measured>
- No silent torch-fallback shim introduced: <pass/fail>
```
