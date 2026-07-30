import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ModelChatBehaviorPanel } from '../ModelChatBehaviorPanel';

vi.mock('../RepairInstallationDialog', () => ({
  RepairInstallationDialog: ({ isOpen }: { isOpen: boolean }) =>
    isOpen ? <div data-testid="repair-dialog">repair</div> : null,
}));

vi.mock('../AdoptInstallationDialog', () => ({
  AdoptInstallationDialog: ({ isOpen }: { isOpen: boolean }) =>
    isOpen ? <div data-testid="adopt-dialog">adopt</div> : null,
}));

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
  it('renders compact lifecycle controls without the advanced editor', async () => {
    const user = userEvent.setup();
    render(<ModelChatBehaviorPanel detail={detail as never} />);

    expect(screen.getByText('llama_qwen')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Repair' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Adopt curated' })).toBeInTheDocument();
    expect(screen.queryByText('Edit chat behavior (advanced)')).not.toBeInTheDocument();
    expect(screen.queryByText('Sampling Parameters JSON')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Repair' }));
    expect(screen.getByTestId('repair-dialog')).toBeInTheDocument();
  });
});
