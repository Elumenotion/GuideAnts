import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ModelChatBehaviorPanel } from '../ModelChatBehaviorPanel';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      updateModel: vi.fn(),
    },
  },
}));

vi.mock('../RepairInstallationDialog', () => ({
  RepairInstallationDialog: () => null,
}));

vi.mock('../AdoptInstallationDialog', () => ({
  AdoptInstallationDialog: () => null,
}));

import { api } from '../../../../services/api';

const detail = {
  modelId: 'llama_qwen',
  routerModelId: 'Qwen3.5-9B-GGUF',
  catalogId: 'qwen3.5-9b',
  catalogVersion: '1',
  runtimeState: 'unloaded',
  loaded: false,
  targetDirectory: '/models-local/llama/Qwen3.5-9B-GGUF',
  modelArtifacts: [],
  projectorArtifacts: [],
  routerPresetSnapshot: {},
  catalogModel: {
    modelId: 'llama_qwen',
    displayName: 'Qwen 3.5 9B',
    provider: 'llama-cpp',
    description: '',
    reasoningChoicesJson: '["medium"]',
    runtimeConfigJson: '{"routerModelId":"Qwen3.5-9B-GGUF"}',
    combineSystemAndDeveloperMessages: true,
    thoughtBlockPattern: '',
    samplingParametersJson: '{}',
    thinkingControlJson: '{"defaultChoice":"medium","choiceActions":{"medium":[]}}',
    requestFieldsWhenToolsPresentJson: '{}',
    isActive: true,
    displayOrder: 0,
    created: '2026-01-01T00:00:00Z',
  },
} as const;

describe('ModelChatBehaviorPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders model identity and repair affordance', async () => {
    render(<ModelChatBehaviorPanel detail={detail as never} />);

    expect(await screen.findByText('llama_qwen')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Repair' })).toBeInTheDocument();
    expect(screen.queryByLabelText(/top_k/i)).not.toBeInTheDocument();
  });

  it('saves model-owned behavior through updateModel', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.updateModel).mockResolvedValue({
      modelId: 'llama_qwen',
      displayName: 'Qwen 3.5 9B',
      provider: 'llama-cpp',
      combineSystemAndDeveloperMessages: true,
      samplingParametersJson: '{"temperature":0.5}',
      thinkingControlJson: '{"defaultChoice":"medium","choiceActions":{"medium":[]}}',
      requestFieldsWhenToolsPresentJson: '{}',
      isActive: true,
      created: '2026-01-01T00:00:00Z',
    } as never);

    render(<ModelChatBehaviorPanel detail={detail as never} />);
    await screen.findByText('Qwen 3.5 9B');

    await user.click(screen.getByText('Edit chat behavior (advanced)'));
    const samplingField = screen.getAllByDisplayValue('{}')[0];
    fireEvent.change(samplingField, { target: { value: '{"temperature":0.5}' } });
    await user.click(screen.getByRole('button', { name: 'Save model behavior' }));
    await user.click(screen.getByTestId('confirm'));

    await waitFor(() => {
      expect(api.settings.updateModel).toHaveBeenCalledWith(
        'llama_qwen',
        expect.objectContaining({
          samplingParametersJson: '{"temperature":0.5}',
          runtimeConfigJson: '{"routerModelId":"Qwen3.5-9B-GGUF"}',
        }),
      );
    });
  });

  it('confirms before saving model behavior', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.updateModel).mockResolvedValue({
      modelId: 'llama_qwen',
      displayName: 'Qwen 3.5 9B',
      provider: 'llama-cpp',
      combineSystemAndDeveloperMessages: true,
      samplingParametersJson: '{}',
      thinkingControlJson: '{"defaultChoice":"medium","choiceActions":{"medium":[]}}',
      requestFieldsWhenToolsPresentJson: '{}',
      isActive: true,
      created: '2026-01-01T00:00:00Z',
    } as never);

    render(<ModelChatBehaviorPanel detail={detail as never} />);
    await screen.findByText('Qwen 3.5 9B');

    await user.click(screen.getByText('Edit chat behavior (advanced)'));
    await user.click(screen.getByRole('button', { name: 'Save model behavior' }));

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Save model behavior' })).toBeInTheDocument();
    expect(api.settings.updateModel).not.toHaveBeenCalled();

    await user.click(screen.getByTestId('confirm'));

    await waitFor(() => {
      expect(api.settings.updateModel).toHaveBeenCalledWith('llama_qwen', expect.any(Object));
    });
  });
});
