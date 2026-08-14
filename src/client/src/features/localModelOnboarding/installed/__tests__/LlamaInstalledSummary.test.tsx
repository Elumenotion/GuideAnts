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
