---
name: audiocpp-asr-extended
description: "Transcription beyond the GuideAnts wrapper contract: transcribe workspace files by path (no upload, no 50 MB gateway cap), pass language hints, and sideload other qwen3-family ASR snapshots from Hugging Face through the wrapper. Use when a transcription needs a language hint, the file is large, or the user wants to try an ASR model the catalog doesn't list."
metadata:
  guideants:
    enabled: true
    display_order: 33
    requires_toolsets: [sandbox]
---

# audio.cpp extended ASR (experimental)

The GuideAnts ASR wrapper (`127.0.0.1:8082`) takes multipart uploads with no
language control. The raw engine it spawns (`127.0.0.1:18082`) accepts a
server-local *path* and a `language` hint — and the sandbox shares its
filesystem, so any workspace file qualifies. The wrapper also loads bare model
directories the catalog doesn't list (unlike the strictly-gated TTS wrapper).

## Preflight

```bash
python3 Output/Skills/audiocpp-asr-extended/scripts/preflight.py --for asr-extended
```

Trust its verdict over this document. `open: false` means no ASR model is
loaded — ask the user to load one via GuideAnts Settings → Local models.
Warnings tell you whether the sideload path (below) is available.

## Transcribing by path (with language hint)

```bash
python3 Output/Skills/audiocpp-asr-extended/scripts/engine_tool.py transcribe \
  Output/uploads/clip.wav [--language de]
```

- No upload and no gateway size cap — the engine reads the file in place.
- The engine model id is always `qwen3-asr` (fixed by the wrapper), whatever
  snapshot is actually loaded.
- Input should be WAV for the raw engine; for other formats, either convert
  with ffmpeg first or use the wrapper upload (`POST :8082/transcribe`,
  multipart field `audio`), which re-encodes to 16 kHz mono itself.

## Sideloading a non-catalog ASR snapshot

The ASR wrapper's `/admin/load` accepts any bare directory under
`/models-local/asr` whose leaf name matches the `model_id`. The engine family
defaults to `qwen3_asr`, so **only qwen3_asr-family snapshots work** (other
families would need the `GA_ASR_ENGINE_FAMILY` env at service start — a compose
change, out of skill scope).

```bash
python3 Output/Skills/audiocpp-asr-extended/scripts/fetch_model.py <hf-repo-id> \
  --dest /models-local/asr/<DirName>
curl -s -X POST http://127.0.0.1:8082/admin/load \
  -H "Content-Type: application/json" -d '{"model_id": "<DirName>"}'
```

**Warning — this replaces the user's loaded ASR model.** Ask before doing it,
and offer to restore afterwards:

```bash
curl -s -X POST http://127.0.0.1:8082/admin/load \
  -H "Content-Type: application/json" -d '{"model_id": "qwen3_asr_0_6b"}'
```

Note: GuideAnts' desired-state reconciler owns wrapper load state — a sideload
can be silently undone by a later reconcile (e.g. after a local-LLM switch).
Treat sideloads as session-scoped experiments, not durable configuration.

## Related

Speaker-labeled transcripts ("who said what") are the **audiocpp-diarize**
skill.

## Reporting

End by telling the user what worked and what was blocked, quoting the
preflight/engine evidence.
