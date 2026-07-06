# Manifest schemas

JSON Schema for catalogs that drive **model list, download sources, and per-model configuration metadata** (`voiceInput`, `gated`, `capabilities`, etc.).

**Product ids must match** [INVENTORY.md](../INVENTORY.md).

## Runtime copies (edit these)

| Service | Manifest path |
|---------|-----------------|
| Embeddings | `docker/build/guideants-ai/emb-service/catalog/manifest.json` |
| ASR | `docker/build/guideants-ai/asr-service/catalog/manifest.json` |
| TTS | `docker/build/guideants-ai/tts-service/catalog/manifest.json` |
| Voice pack | `docker/build/guideants-ai/voice-pack/manifest.json` |

## Schemas

| File | Applies to |
|------|------------|
| [schema.model.json](./schema.model.json) | `task: emb \| asr \| tts` catalog entries |
| [schema.voice-pack.json](./schema.voice-pack.json) | Voice preset manifest |

TTS entries **require** `family` and `voiceInput` per schema `allOf` rules.

## Validate

```powershell
npx --yes ajv-cli validate -s docs/native-ai-migration/catalog/schema.model.json -d docker/build/guideants-ai/tts-service/catalog/manifest.json

python docker/build/guideants-ai/scripts/check-voice-pack-attribution.py docker/build/guideants-ai/voice-pack
```

After changing manifests, update [STATE.md](../STATE.md) manifest completeness table.
