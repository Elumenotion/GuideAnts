import { describe, it, expect } from 'vitest';
import type {
  HuggingFaceRepositoryFileDto,
  HuggingFaceRepositoryListingDto,
} from '../../../../types/settings';
import {
  sdDiffusionClassifier,
  sdTextEncoderClassifier,
  sdVaeClassifier,
} from './sdClassifier';

function file(
  path: string,
  extra: Partial<HuggingFaceRepositoryFileDto> = {},
): HuggingFaceRepositoryFileDto {
  return {
    path,
    size: 100,
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
): { repository: string; listing: HuggingFaceRepositoryListingDto } {
  return {
    repository: 'acme/sd',
    listing: {
      repository: 'acme/sd',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files,
    },
  };
}

describe('sdDiffusionClassifier', () => {
  it('auto-selects a single unambiguous flux file', () => {
    const files = [file('flux1-dev.safetensors'), file('vae/ae.safetensors')];
    const c = ctx(files);
    const result = sdDiffusionClassifier(files, c).diffusion;
    expect(result.candidates.map((c) => c.path)).toEqual(['flux1-dev.safetensors']);
    expect(result.autoSelect).toBe('flux1-dev.safetensors');
  });

  it('does not auto-select when multiple candidates match', () => {
    const files = [
      file('flux1-dev.safetensors'),
      file('flux1-schnell.safetensors'),
    ];
    const c = ctx(files);
    const result = sdDiffusionClassifier(files, c).diffusion;
    expect(result.candidates).toHaveLength(2);
    expect(result.autoSelect).toBeNull();
  });

  it('disables sharded candidates and skips them for auto-select', () => {
    const files = [
      file('flux1-dev-00001-of-00002.safetensors', {
        sharded: true,
        shardIndex: 1,
        shardTotal: 2,
      }),
      file('flux1-dev-00002-of-00002.safetensors', {
        sharded: true,
        shardIndex: 2,
        shardTotal: 2,
      }),
    ];
    const c = ctx(files);
    const result = sdDiffusionClassifier(files, c).diffusion;
    expect(result.candidates).toHaveLength(2);
    for (const candidate of result.candidates) {
      expect(candidate.disabled).toBe(true);
      expect(candidate.disabledReason).toMatch(/sharded/i);
    }
    expect(result.autoSelect).toBeNull();
  });

  it('returns an empty result with a helpful message when nothing matches', () => {
    const files = [file('README.md'), file('LICENSE')];
    const c = ctx(files);
    const result = sdDiffusionClassifier(files, c).diffusion;
    expect(result.candidates).toHaveLength(0);
    expect(result.autoSelect).toBeNull();
    expect(result.emptyMessage).toBeTruthy();
  });

  it('rejects VAE / text-encoder weights for the diffusion role', () => {
    const files = [
      file('vae/ae.safetensors'),
      file('text_encoder/clip.safetensors'),
    ];
    const c = ctx(files);
    const result = sdDiffusionClassifier(files, c).diffusion;
    expect(result.candidates).toHaveLength(0);
  });
});

describe('sdVaeClassifier', () => {
  it('picks a lone VAE file', () => {
    const files = [file('flux1-dev.safetensors'), file('vae.safetensors')];
    const c = ctx(files);
    const result = sdVaeClassifier(files, c).vae;
    expect(result.candidates.map((c) => c.path)).toEqual(['vae.safetensors']);
    expect(result.autoSelect).toBe('vae.safetensors');
  });

  it('does not auto-select when multiple VAE candidates are present', () => {
    const files = [file('vae.safetensors'), file('ae_decoder.safetensors')];
    const c = ctx(files);
    const result = sdVaeClassifier(files, c).vae;
    expect(result.candidates).toHaveLength(2);
    expect(result.autoSelect).toBeNull();
  });
});

describe('sdTextEncoderClassifier', () => {
  it('picks a single T5/CLIP file', () => {
    const files = [file('text_encoder/clip_l.safetensors')];
    const c = ctx(files);
    const result = sdTextEncoderClassifier(files, c).textEncoder;
    expect(result.candidates.map((c) => c.path)).toEqual([
      'text_encoder/clip_l.safetensors',
    ]);
    expect(result.autoSelect).toBe('text_encoder/clip_l.safetensors');
  });

  it('remains empty for a repo with only diffusion weights', () => {
    const files = [file('flux1-dev.safetensors')];
    const c = ctx(files);
    const result = sdTextEncoderClassifier(files, c).textEncoder;
    expect(result.candidates).toHaveLength(0);
    expect(result.autoSelect).toBeNull();
  });
});
