import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ModelChatBehaviorPanel } from '../ModelChatBehaviorPanel';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      getRuntimeProfile: vi.fn(),
      updateRuntimeProfile: vi.fn(),
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
  modelId: 'llama/qwen',
  routerModelId: 'Qwen3.5-9B-GGUF',
  runtimeProfileId: 'qwen3_6',
  catalogId: 'qwen3.5-9b',
  catalogVersion: '1',
  runtimeState: 'unloaded',
  loaded: false,
  targetDirectory: '/models-local/llama/Qwen3.5-9B-GGUF',
  modelArtifacts: [],
  projectorArtifacts: [],
  routerPresetSnapshot: {},
} as const;

describe('ModelChatBehaviorPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getRuntimeProfile).mockResolvedValue({
      profileId: 'qwen3_6',
      displayName: 'Qwen 3.6',
      description: '',
      combineSystemAndDeveloperMessages: true,
      thoughtBlockPattern: '',
      samplingParametersJson: '{}',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      providers: ['llama-cpp'],
      created: '2026-01-01T00:00:00Z',
    });
  });

  it('renders bound profile id and repair affordance', async () => {
    render(<ModelChatBehaviorPanel detail={detail as never} />);

    expect(await screen.findByText('qwen3_6')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Repair' })).toBeInTheDocument();
    expect(screen.queryByLabelText(/top_k/i)).not.toBeInTheDocument();
  });

  it('saves profile document through updateRuntimeProfile', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.updateRuntimeProfile).mockResolvedValue({
      profileId: 'qwen3_6',
      displayName: 'Qwen 3.6',
      combineSystemAndDeveloperMessages: true,
      samplingParametersJson: '{"temperature":0.5}',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      providers: ['llama-cpp'],
      created: '2026-01-01T00:00:00Z',
    } as never);

    render(<ModelChatBehaviorPanel detail={detail as never} />);
    await screen.findByText('Qwen 3.6');

    await user.click(screen.getByText('Edit profile document (advanced)'));
    const samplingField = screen.getAllByDisplayValue('{}')[0];
    fireEvent.change(samplingField, { target: { value: '{"temperature":0.5}' } });
    await user.click(screen.getByRole('button', { name: 'Save profile document' }));

    await waitFor(() => {
      expect(api.settings.updateRuntimeProfile).toHaveBeenCalledWith(
        'qwen3_6',
        expect.objectContaining({
          samplingParametersJson: '{"temperature":0.5}',
        }),
      );
    });
  });

  it('confirms before saving a shared runtime profile', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.updateRuntimeProfile).mockResolvedValue({
      profileId: 'qwen3_6',
      displayName: 'Qwen 3.6',
      combineSystemAndDeveloperMessages: true,
      samplingParametersJson: '{}',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      providers: ['llama-cpp'],
      created: '2026-01-01T00:00:00Z',
    } as never);

    render(<ModelChatBehaviorPanel detail={detail as never} sharedProfileModelCount={2} />);
    await screen.findByText('Qwen 3.6');

    await user.click(screen.getByText('Edit profile document (advanced)'));
    await user.click(screen.getByRole('button', { name: 'Save profile document' }));

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Update shared profile' })).toBeInTheDocument();
    expect(api.settings.updateRuntimeProfile).not.toHaveBeenCalled();

    await user.click(screen.getByTestId('confirm'));

    await waitFor(() => {
      expect(api.settings.updateRuntimeProfile).toHaveBeenCalledWith('qwen3_6', expect.any(Object));
    });
  });
});
