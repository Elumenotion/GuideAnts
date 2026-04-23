import type { HuggingFaceRepositoryFileDto } from '../../../../types/settings';
import type { ClassifyPreviewFn, SnapshotPreview } from './repositoryPickerTypes';

type BucketId = 'weights' | 'tokenizer' | 'config' | 'other';

const WEIGHT_EXTENSIONS = /\.(safetensors|bin|pt|onnx)$/i;
const WEIGHT_NAME_PATTERNS = [/pytorch_model/i, /model\.safetensors/i];

const TOKENIZER_LEAVES = new Set(
  [
    'tokenizer.json',
    'tokenizer_config.json',
    'vocab.json',
    'merges.txt',
    'added_tokens.json',
    'special_tokens_map.json',
    'normalizer.json',
    'vocab.txt',
    'spm.model',
  ].map((name) => name.toLowerCase()),
);

const CONFIG_LEAVES = new Set(
  [
    'config.json',
    'generation_config.json',
    'preprocessor_config.json',
    'feature_extractor.json',
    'feature_extractor_config.json',
    'processor_config.json',
    'model_index.json',
    'scheduler_config.json',
  ].map((name) => name.toLowerCase()),
);

const CONFIG_NAME_PATTERNS = [/^feature_extractor/i, /config\.json$/i];

// Sizes over this threshold raise the "very large snapshot" warning banner so
// operators see they are about to pull a big directory. 10 GiB matches the
// threshold called out in the ASR/TTS requirements.
const LARGE_WEIGHTS_WARN_BYTES = 10 * 1024 * 1024 * 1024;

function leafName(path: string): string {
  const idx = path.lastIndexOf('/');
  return idx >= 0 ? path.substring(idx + 1) : path;
}

function bucketFor(file: HuggingFaceRepositoryFileDto): BucketId {
  const path = file.path;
  const leaf = leafName(path).toLowerCase();
  if (TOKENIZER_LEAVES.has(leaf)) {
    return 'tokenizer';
  }
  if (CONFIG_LEAVES.has(leaf)) {
    return 'config';
  }
  if (CONFIG_NAME_PATTERNS.some((re) => re.test(leaf))) {
    return 'config';
  }
  if (WEIGHT_EXTENSIONS.test(leaf)) {
    return 'weights';
  }
  if (WEIGHT_NAME_PATTERNS.some((re) => re.test(leaf))) {
    return 'weights';
  }
  return 'other';
}

const BUCKET_LABELS: Record<BucketId, string> = {
  weights: 'weights',
  tokenizer: 'tokenizer',
  config: 'config',
  other: 'other',
};

/**
 * Shared classifier for preview-only services (ASR / TTS / Embeddings). It
 * sorts the raw file listing into four deterministic buckets — weights,
 * tokenizer, config, other — plus a total-size aggregate and a warning
 * when total weights exceed {@link LARGE_WEIGHTS_WARN_BYTES}.
 *
 * Classification is driven purely by file leaf name and extension, not by
 * any server-side llama-specific {@code category} value, so whole-snapshot
 * services do not bleed llama-cpp heuristics into their preview.
 */
export const snapshotPreviewClassifier: ClassifyPreviewFn = (files) => {
  const grouped: Record<BucketId, SnapshotPreview['buckets'][number]['files']> = {
    weights: [],
    tokenizer: [],
    config: [],
    other: [],
  };
  let totalSize = 0;
  let weightsSize = 0;
  for (const file of files) {
    const bucketId = bucketFor(file);
    grouped[bucketId].push({ path: file.path, size: file.size ?? null });
    if (typeof file.size === 'number' && file.size > 0) {
      totalSize += file.size;
      if (bucketId === 'weights') {
        weightsSize += file.size;
      }
    }
  }

  const buckets: SnapshotPreview['buckets'] = (Object.keys(grouped) as BucketId[]).map((id) => ({
    id,
    label: BUCKET_LABELS[id],
    files: grouped[id].slice().sort((a, b) => a.path.localeCompare(b.path)),
  }));

  const warnings: string[] = [];
  if (weightsSize > LARGE_WEIGHTS_WARN_BYTES) {
    warnings.push(
      `Total weights size exceeds 10 GiB. Make sure there is enough disk before starting the download.`,
    );
  }
  return { buckets, totalSizeBytes: totalSize, warnings };
};

/**
 * Wrap a preview classifier so each file row can have additional badges
 * appended. Used by TTS to surface a <c>voice</c> badge next to files
 * under <c>voices/</c> without duplicating the whole classifier.
 */
export function withSnapshotExtraBadges(
  base: ClassifyPreviewFn,
  extraBadges: (file: HuggingFaceRepositoryFileDto) => string[],
): ClassifyPreviewFn {
  return (files, context) => {
    const preview = base(files, context);
    const fileByPath = new Map(files.map((f) => [f.path, f]));
    const annotated: SnapshotPreview['buckets'] = preview.buckets.map((bucket) => ({
      ...bucket,
      files: bucket.files.map((entry) => {
        const source = fileByPath.get(entry.path);
        if (!source) {
          return entry;
        }
        const extra = extraBadges(source);
        if (!extra || extra.length === 0) {
          return entry;
        }
        const combined = [...(entry.badges ?? []), ...extra];
        return { ...entry, badges: combined };
      }),
    }));
    return { ...preview, buckets: annotated };
  };
}
