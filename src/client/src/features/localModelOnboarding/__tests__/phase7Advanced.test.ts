import { describe, expect, it } from 'vitest';
import { buildGgufArtifactGroups } from '../artifactGroups';
import { buildAliasIniPreview, buildEffectivePresetRecord, validateAliasPresetRows } from '../routerPreset';

describe('routerPreset', () => {
  it('accepts model-scoped llama-server preset keys on the alias row', () => {
    const errors = validateAliasPresetRows([
      { key: 'spec-type', value: 'draft-mtp' },
      { key: 'reasoning-budget', value: '4096' },
    ]);
    expect(errors).toEqual([]);
  });

  it('rejects router-shell keys on the alias row', () => {
    const errors = validateAliasPresetRows([{ key: 'models-preset', value: '/ini/path' }]);
    expect(errors.some((error) => error.includes('router-shell'))).toBe(true);
  });

  it('rejects process/env-owned keys on the alias row', () => {
    const errors = validateAliasPresetRows([
      { key: 'n-gpu-layers', value: '999' },
      { key: 'no-mmap', value: 'true' },
      { key: 'parallel', value: '2' },
    ]);
    expect(errors.some((error) => error.includes('process/env-owned'))).toBe(true);
  });

  it('builds INI preview', () => {
    const preview = buildAliasIniPreview('alias-a', { 'ctx-size': '8192' });
    expect(preview).toContain('[alias-a]');
    expect(preview).toContain('ctx-size = 8192');
  });

  it('builds effective preset from dedicated managed fields', () => {
    const preset = buildEffectivePresetRecord(
      [{ key: 'spec-type', value: 'draft-mtp' }],
      '131072',
      '',
    );
    expect(preset).toEqual({ 'ctx-size': '131072', 'spec-type': 'draft-mtp' });
  });
});

describe('artifactGroups', () => {
  it('groups ordered shards into one artifact group', () => {
    const groups = buildGgufArtifactGroups([
      {
        path: 'model-00001-of-00002.gguf',
        category: 'gguf',
        quantLabel: 'Q4_K_M',
        sharded: true,
        shardIndex: 1,
        shardTotal: 2,
        size: 100,
      },
      {
        path: 'model-00002-of-00002.gguf',
        category: 'gguf',
        quantLabel: 'Q4_K_M',
        sharded: true,
        shardIndex: 2,
        shardTotal: 2,
        size: 100,
      },
    ]);
    expect(groups).toHaveLength(1);
    expect(groups[0]?.files).toEqual([
      'model-00001-of-00002.gguf',
      'model-00002-of-00002.gguf',
    ]);
  });
});
