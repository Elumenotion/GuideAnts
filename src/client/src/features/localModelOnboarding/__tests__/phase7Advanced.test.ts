import { describe, expect, it } from 'vitest';
import { buildGgufArtifactGroups } from '../artifactGroups';
import { buildAliasIniPreview, validateAliasPresetRows } from '../routerPreset';

describe('routerPreset', () => {
  it('accepts llama-server preset keys on the model row', () => {
    const errors = validateAliasPresetRows([{ key: 'parallel', value: '4' }]);
    expect(errors).toEqual([]);
  });

  it('builds INI preview', () => {
    const preview = buildAliasIniPreview('alias-a', { 'ctx-size': '8192' });
    expect(preview).toContain('[alias-a]');
    expect(preview).toContain('ctx-size = 8192');
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
