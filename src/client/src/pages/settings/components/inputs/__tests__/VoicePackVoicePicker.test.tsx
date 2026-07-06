import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { VoicePackVoicePicker } from '../VoicePackVoicePicker';

vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      localModels: {
        voicePackOutcome: vi.fn(),
      },
    },
  },
}));

// eslint-disable-next-line @typescript-eslint/no-var-requires
import { api } from '../../../../../services/api';

describe('VoicePackVoicePicker', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it('loads presets from the API into a dropdown and selects a voice', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    (api.settings.localModels.voicePackOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        voices: [
          { voiceId: 'af_alloy', displayName: 'af alloy', language: 'en', accent: 'en-US' },
          { voiceId: 'am_adam', displayName: 'am adam', language: 'en', accent: 'en-US' },
        ],
      },
    });

    render(<VoicePackVoicePicker value="" onChange={onChange} />);

    await waitFor(() => {
      expect(api.settings.localModels.voicePackOutcome).toHaveBeenCalledWith('SpeechSynthesis');
      expect(screen.getByRole('option', { name: /am_adam — am adam/i })).toBeInTheDocument();
    });

    await user.selectOptions(screen.getByRole('combobox', { name: /Reference voice/i }), 'am_adam');
    expect(onChange).toHaveBeenCalledWith('am_adam');
  });

  it('shows an unavailable state instead of a free-text fallback', async () => {
    (api.settings.localModels.voicePackOutcome as any).mockResolvedValue({
      kind: 'error',
      message: 'unavailable',
    });

    render(<VoicePackVoicePicker value="custom_voice" onChange={vi.fn()} />);

    await waitFor(() => {
      expect(screen.getByText(/Voice pack presets could not be loaded/i)).toBeInTheDocument();
    });
    expect(screen.getByRole('combobox', { name: /Reference voice/i })).toBeDisabled();
    expect(screen.queryByDisplayValue('custom_voice')).not.toBeInTheDocument();
  });

  it('keeps an orphan saved value visible in the dropdown', async () => {
    (api.settings.localModels.voicePackOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        voices: [{ voiceId: 'af_alloy', displayName: 'af alloy' }],
      },
    });

    render(<VoicePackVoicePicker value="legacy_voice" onChange={vi.fn()} />);

    await waitFor(() => {
      expect(screen.getByRole('option', { name: /legacy_voice \(not in voice pack\)/i })).toBeInTheDocument();
    });
    expect(screen.getByText(/not in the baked pack/i)).toBeInTheDocument();
  });
});
