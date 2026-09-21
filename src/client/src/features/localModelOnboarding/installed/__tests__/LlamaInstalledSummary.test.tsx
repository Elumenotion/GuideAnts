import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { LlamaInstalledSummary } from '../LlamaInstalledSummary';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      getLlamaInstallationDetail: vi.fn(),
      getLlamaRouterEntries: vi.fn(),
    },
  },
}));

vi.mock('../AliasPresetSavePanel', () => ({
  AliasPresetSavePanel: () => <div data-testid="alias-preset-save-panel" />,
}));

vi.mock('../ModelChatBehaviorPanel', () => ({
  ModelChatBehaviorPanel: () => <div data-testid="model-chat-behavior-panel" />,
}));

import { api } from '../../../../services/api';

describe('LlamaInstalledSummary', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getLlamaInstallationDetail).mockResolvedValue({
      modelId: 'llama/qwen',
      routerModelId: 'Qwen3.5-9B-GGUF',
      catalogId: 'qwen3.5-9b',
      runtimeState: 'unloaded',
      loaded: false,
      targetDirectory: '/models-local/llama/Qwen3.5-9B-GGUF',
      modelArtifacts: [],
      projectorArtifacts: [],
      routerPresetSnapshot: {},
    } as never);
    vi.mocked(api.settings.getLlamaRouterEntries).mockResolvedValue({ entries: [] } as never);
  });

  it('renders layer 2 and layer 3 panels without management-mode box', async () => {
    render(<LlamaInstalledSummary modelId="llama/qwen" onOperationStarted={vi.fn()} />);

    expect(await screen.findByTestId('alias-preset-save-panel')).toBeInTheDocument();
    expect(screen.getByTestId('model-chat-behavior-panel')).toBeInTheDocument();
    expect(screen.queryByText(/Management mode/i)).not.toBeInTheDocument();
  });

  it('still renders the preset editor when live router entries fail', async () => {
    vi.mocked(api.settings.getLlamaRouterEntries).mockRejectedValue(new Error('Llama router entries unavailable'));

    render(<LlamaInstalledSummary modelId="llama/qwen" onOperationStarted={vi.fn()} />);

    expect(await screen.findByTestId('alias-preset-save-panel')).toBeInTheDocument();
    expect(screen.getByText(/router entries unavailable/i)).toBeInTheDocument();
  });
});

describe('LlamaInstalledSummary without installation provenance', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getLlamaInstallationDetail).mockRejectedValue(
      new Error("Model 'qwen3.6-35b-a3b-mtp-max' has no installation provenance record."),
    );
    vi.mocked(api.settings.getLlamaRouterEntries).mockResolvedValue({
      entries: [
        {
          alias: 'qwen3.6-max',
          modelPath: '/models-local/llama/Qwen3.6-35B-A3B-MTP.gguf',
          mmprojPath: '',
          hasModelFile: true,
          hasMmprojFile: false,
          preset: { 'ctx-size': '32768' },
        },
      ],
    } as never);
  });

  it('renders the preset editor for a row-owned stack when provenance is missing', async () => {
    render(
      <LlamaInstalledSummary
        modelId="qwen3.6-35b-a3b-mtp-max"
        runtimeConfigJson={JSON.stringify({ routerModelId: 'qwen3.6-max', stackBaseUrl: 'http://192.0.2.1:8112' })}
        onOperationStarted={vi.fn()}
      />,
    );

    expect(await screen.findByTestId('alias-preset-save-panel')).toBeInTheDocument();
    expect(screen.getByText(/written to the row's stack/i)).toBeInTheDocument();
    expect(screen.queryByText(/no installation provenance record/i)).not.toBeInTheDocument();
  });

  it('keeps the provenance error for rows without a row-owned stack', async () => {
    render(
      <LlamaInstalledSummary
        modelId="qwen3.6-35b-a3b-mtp-local"
        runtimeConfigJson={JSON.stringify({ routerModelId: 'qwen3.6-local' })}
        onOperationStarted={vi.fn()}
      />,
    );

    expect(await screen.findByText(/no installation provenance record/i)).toBeInTheDocument();
    expect(screen.queryByTestId('alias-preset-save-panel')).not.toBeInTheDocument();
  });
});
