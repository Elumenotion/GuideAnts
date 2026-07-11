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
        }),
      );
    });
  });
});
