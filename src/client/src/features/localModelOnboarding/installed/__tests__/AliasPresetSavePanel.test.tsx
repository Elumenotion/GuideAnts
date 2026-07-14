import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AliasPresetSavePanel } from '../AliasPresetSavePanel';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      putLlamaRouterEntry: vi.fn(),
    },
  },
}));

import { api } from '../../../../services/api';

const routerEntry = {
  alias: 'Qwen3.5-9B-GGUF',
  modelPath: '/models-local/llama/Qwen3.5-9B-GGUF/model.gguf',
  mmprojPath: '',
  hasModelFile: true,
  hasMmprojFile: false,
  contextSize: 131072,
  cacheRamMib: null,
  preset: { 'ctx-size': '131072' },
};

const mtpRouterEntry = {
  alias: 'Qwen3.6-27B-MTP-GGUF',
  modelPath: '/models-local/llama/Qwen3.6-27B-MTP-GGUF/model.gguf',
  mmprojPath: '/models-local/llama/Qwen3.6-27B-MTP-GGUF/mmproj.gguf',
  hasModelFile: true,
  hasMmprojFile: true,
  contextSize: null,
  cacheRamMib: null,
  preset: {
    'image-min-tokens': '1024',
    'spec-draft-n-max': '2',
    'spec-type': 'draft-mtp',
  },
};

describe('AliasPresetSavePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('saves edited preset through putLlamaRouterEntry', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.putLlamaRouterEntry).mockResolvedValue(undefined as never);

    render(
      <AliasPresetSavePanel
        alias="Qwen3.5-9B-GGUF"
        routerEntry={routerEntry}
        fallbackPreset={{}}
      />,
    );

    const ctxInput = screen.getByPlaceholderText('e.g. 131072');
    await user.click(ctxInput);
    await user.keyboard('{Control>}a{/Control}65536');
    await user.click(screen.getByRole('button', { name: 'Save router preset' }));

    await waitFor(() => {
      expect(api.settings.putLlamaRouterEntry).toHaveBeenCalledWith(
        'Qwen3.5-9B-GGUF',
        expect.objectContaining({
          alias: 'Qwen3.5-9B-GGUF',
          presetMode: 'merge',
          contextSize: 65536,
          preset: expect.objectContaining({ 'ctx-size': '65536' }),
        }),
      );
    });
  });

  it('shows context size in INI preview when only the dedicated field is set', async () => {
    const user = userEvent.setup();
    render(
      <AliasPresetSavePanel
        alias="Qwen3.6-27B-MTP-GGUF"
        routerEntry={mtpRouterEntry}
        fallbackPreset={{}}
      />,
    );

    expect(screen.queryByText(/ctx-size = /)).not.toBeInTheDocument();

    const ctxInput = screen.getByPlaceholderText('e.g. 131072');
    await user.type(ctxInput, '131072');

    const preview = screen.getByText('INI preview').parentElement?.querySelector('pre');
    expect(preview?.textContent).toContain('ctx-size = 131072');
    expect(preview?.textContent).toContain('spec-type = draft-mtp');
  });

  it('clears context size without reusing the prior router entry value', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.putLlamaRouterEntry).mockResolvedValue(undefined as never);

    render(
      <AliasPresetSavePanel
        alias="Qwen3.5-9B-GGUF"
        routerEntry={routerEntry}
        fallbackPreset={{}}
      />,
    );

    const ctxInput = screen.getByPlaceholderText('e.g. 131072');
    await user.clear(ctxInput);
    expect(ctxInput).toHaveValue('');
    await user.click(screen.getByRole('button', { name: 'Save router preset' }));

    await waitFor(() => {
      expect(api.settings.putLlamaRouterEntry).toHaveBeenCalledWith(
        'Qwen3.5-9B-GGUF',
        expect.objectContaining({
          contextSize: null,
          preset: expect.not.objectContaining({ 'ctx-size': expect.anything() }),
        }),
      );
    });
  });
});
