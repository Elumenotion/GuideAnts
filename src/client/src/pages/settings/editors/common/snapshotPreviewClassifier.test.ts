import { describe, it, expect } from 'vitest';
import type {
  HuggingFaceRepositoryFileDto,
  HuggingFaceRepositoryListingDto,
} from '../../../../types/settings';
import {
  snapshotPreviewClassifier,
  withSnapshotExtraBadges,
} from './snapshotPreviewClassifier';

function file(
  path: string,
  size: number | null = null,
  extra: Partial<HuggingFaceRepositoryFileDto> = {},
): HuggingFaceRepositoryFileDto {
  return {
    path,
    size,
    category: 'other',
    quantLabel: null,
    sharded: false,
    shardIndex: null,
    shardTotal: null,
    ...extra,
  };
}

function ctx(
  files: HuggingFaceRepositoryFileDto[],
  repository = 'acme/model',
): { repository: string; listing: HuggingFaceRepositoryListingDto } {
  return {
    repository,
    listing: {
      repository,
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files,
    },
  };
}

function findBucket(
  preview: ReturnType<typeof snapshotPreviewClassifier>,
  id: string,
) {
  const bucket = preview.buckets.find((b) => b.id === id);
  if (!bucket) throw new Error(`bucket ${id} missing`);
  return bucket;
}

describe('snapshotPreviewClassifier', () => {
  it('handles an empty listing', () => {
    const c = ctx([]);
    const preview = snapshotPreviewClassifier(c.listing.files, c);
    expect(preview.buckets).toHaveLength(4);
    for (const bucket of preview.buckets) {
      expect(bucket.files).toEqual([]);
    }
    expect(preview.totalSizeBytes).toBe(0);
    expect(preview.warnings).toEqual([]);
  });

  it('classifies files into weights / tokenizer / config / other', () => {
    const files = [
      file('model.safetensors', 100),
      file('pytorch_model.bin', 200),
      file('tokenizer.json', 1),
      file('tokenizer_config.json', 1),
      file('config.json', 2),
      file('generation_config.json', 2),
      file('README.md', 3),
      file('assets/logo.png', 4),
    ];
    const c = ctx(files);
    const preview = snapshotPreviewClassifier(files, c);

    expect(findBucket(preview, 'weights').files.map((f) => f.path)).toEqual([
      'model.safetensors',
      'pytorch_model.bin',
    ]);
    expect(new Set(findBucket(preview, 'tokenizer').files.map((f) => f.path))).toEqual(
      new Set(['tokenizer.json', 'tokenizer_config.json']),
    );
    expect(findBucket(preview, 'config').files.map((f) => f.path)).toEqual([
      'config.json',
      'generation_config.json',
    ]);
    expect(new Set(findBucket(preview, 'other').files.map((f) => f.path))).toEqual(
      new Set(['README.md', 'assets/logo.png']),
    );

    expect(preview.totalSizeBytes).toBe(313);
    expect(preview.warnings).toEqual([]);
  });

  it('sums total size and flags >10 GiB weights', () => {
    const big = 11 * 1024 * 1024 * 1024;
    const files = [file('model.safetensors', big), file('tokenizer.json', 1)];
    const c = ctx(files);
    const preview = snapshotPreviewClassifier(files, c);
    expect(preview.totalSizeBytes).toBe(big + 1);
    expect(preview.warnings?.[0]).toMatch(/10 GiB/i);
  });

  it('bucket files are sorted deterministically', () => {
    const files = [
      file('z-weight.safetensors', 1),
      file('a-weight.safetensors', 1),
      file('m-weight.safetensors', 1),
    ];
    const c = ctx(files);
    const preview = snapshotPreviewClassifier(files, c);
    expect(findBucket(preview, 'weights').files.map((f) => f.path)).toEqual([
      'a-weight.safetensors',
      'm-weight.safetensors',
      'z-weight.safetensors',
    ]);
  });
});

describe('withSnapshotExtraBadges', () => {
  it('appends extra badges without mutating unrelated buckets', () => {
    const files = [
      file('voices/af_bella.pt', 10),
      file('tokenizer.json', 1),
    ];
    const c = ctx(files);
    const classifier = withSnapshotExtraBadges(snapshotPreviewClassifier, (f) =>
      f.path.startsWith('voices/') ? ['voice'] : [],
    );
    const preview = classifier(files, c);
    const weights = findBucket(preview, 'weights');
    const voiceRow = weights.files.find((f) => f.path === 'voices/af_bella.pt');
    expect(voiceRow?.badges).toEqual(['voice']);
    const tokenizerRow = findBucket(preview, 'tokenizer').files[0];
    expect(tokenizerRow?.badges ?? []).toEqual([]);
  });
});
