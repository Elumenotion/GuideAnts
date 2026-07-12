import type { LlamaCatalogResponseDto, LlamaCatalogQuantsResponseDto } from '../../../types/settings';

export const catalogFixture: LlamaCatalogResponseDto = {
  schemaVersion: 1,
  task: 'llama',
  catalogVersion: '2026-07-10',
  models: [
    {
      id: 'qwen3.6-35b-a3b',
      display: {
        name: 'Qwen 3.6 35B A3B',
        description: 'Curated local Qwen model with vision configuration.',
        labels: ['Text', 'Vision', 'Reasoning', 'Tool use'],
        license: 'Apache-2.0',
        documentationUrl: 'https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF',
      },
      source: {
        repository: 'unsloth/Qwen3.6-35B-A3B-GGUF',
        revision: 'main',
      },
      defaults: {
        catalogModelId: 'qwen3.6-35b-a3b-local',
        routerModelId: 'Qwen3.6-35B-A3B-GGUF',
        runtimeProfileId: 'qwen3_6',
        targetDirectory: 'Qwen3.6-35B-A3B-GGUF',
        mmproj: { path: 'mmproj-F16.gguf' },
        routerPreset: {
          'ctx-size': '131072',
          'image-min-tokens': '1024',
        },
      },
      quantMetadata: {
        recommendedLabels: ['UD-Q4_K_XL', 'UD-Q5_K_XL', 'Q4_K_M'],
      },
      hardwareNotes: {
        summary: 'Requires a GPU with at least 24GB VRAM for recommended quants.',
        contextClass: 'large',
      },
    },
    {
      id: 'qwen3.6-35b-a3b-mtp',
      display: {
        name: 'Qwen 3.6 35B A3B MTP',
        description: 'Curated local Qwen model with MTP and vision configuration.',
        labels: ['Text', 'Vision', 'Reasoning', 'Tool use', 'MTP'],
        license: 'Apache-2.0',
        documentationUrl: 'https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF',
      },
      source: {
        repository: 'unsloth/Qwen3.6-35B-A3B-MTP-GGUF',
        revision: 'main',
      },
      defaults: {
        catalogModelId: 'qwen3.6-35b-a3b-mtp-local',
        routerModelId: 'Qwen3.6-35B-A3B-MTP-GGUF',
        runtimeProfileId: 'qwen3_6',
        targetDirectory: 'Qwen3.6-35B-A3B-MTP-GGUF',
        mmproj: { path: 'mmproj-F16.gguf' },
        routerPreset: {
          'ctx-size': '131072',
          'image-min-tokens': '1024',
          'spec-type': 'draft-mtp',
          'spec-draft-n-max': '2',
        },
      },
      quantMetadata: {
        recommendedLabels: ['UD-Q4_K_XL'],
      },
      hardwareNotes: {
        summary: 'Draft MTP configuration for faster generation.',
        contextClass: 'large',
      },
    },
  ],
};

export const quantFixture: LlamaCatalogQuantsResponseDto = {
  catalogId: 'qwen3.6-35b-a3b-mtp',
  repository: 'unsloth/Qwen3.6-35B-A3B-MTP-GGUF',
  requestedRevision: 'main',
  resolvedRevision: '8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a',
  quants: [
    {
      id: 'q4_k_m',
      label: 'Q4_K_M',
      totalBytes: 20123456789,
      files: [{ path: 'Qwen3.6-35B-A3B-Q4_K_M.gguf', size: 20123456789 }],
    },
    {
      id: 'q6_k_xl',
      label: 'Q6_K_XL',
      totalBytes: 28765432100,
      files: [
        { path: 'Qwen3.6-35B-A3B-Q6_K_XL-00001-of-00002.gguf', size: 14380000000, shardIndex: 1, shardCount: 2 },
        { path: 'Qwen3.6-35B-A3B-Q6_K_XL-00002-of-00002.gguf', size: 14385432100, shardIndex: 2, shardCount: 2 },
      ],
    },
  ],
  projector: { path: 'mmproj-F16.gguf', size: 900_000_000 },
};

export const curatedRequestFixture = {
  provider: 'llama-cpp',
  catalog: {
    displayName: 'Qwen 3.6 35B A3B MTP',
    isActive: true,
  },
  install: {
    source: 'curated',
    catalogId: 'qwen3.6-35b-a3b-mtp',
    catalogVersion: '2026-07-10',
    quantId: 'q6_k_xl',
    resolvedRevision: '8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a',
  },
};

export const operationFixture = {
  operationId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
  status: 'downloading',
  routerModelId: 'Qwen3.6-35B-A3B-MTP-GGUF',
  progress: 0.42,
  errorMessage: null,
  logLine: 'Downloading Qwen3.6-35B-A3B-Q6_K_XL-00002-of-00002.gguf',
  error: null,
};
