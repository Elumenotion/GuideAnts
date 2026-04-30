import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { TtsToolbarPanel } from '../TtsToolbarPanel';
import { api } from '../../../../services/api';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      services: {
        updateActiveProvider: vi.fn(async (_serviceId: string, providerId: string) => ({
          activeProviderId: providerId,
        })),
      },
      localModels: {
        selectActive: vi.fn(async () => ({})),
        load: vi.fn(async () => ({})),
        unload: vi.fn(async () => ({})),
      },
    },
  },
}));

describe('TtsToolbarPanel', () => {
  it('switches to local provider when selecting an installed model', async () => {
    const user = userEvent.setup();
    render(
      <TtsToolbarPanel
        service={{
          serviceId: 'SpeechSynthesis',
          displayName: 'Speech Synthesis',
          kind: 'tts',
          status: 'ready',
          summary: 'ready',
          activeProviderId: 'SpeechSynthesis.Google.TextToSpeech',
          activeProviderLabel: 'Google Gemini',
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          providerOptions: [
            {
              providerId: 'SpeechSynthesis.Google.TextToSpeech',
              displayName: 'GoogleGeminiApi',
              providerKind: 'Cloud',
              canActivate: true,
              blockers: [],
              providerSection: 'GoogleGeminiApi',
              modelId: 'tts-1',
            },
            {
              providerId: 'SpeechSynthesis.LocalTts.Http',
              displayName: 'LocalServiceHosts:SpeechSynthesisBaseUrl',
              providerKind: 'LocalHttp',
              canActivate: true,
              blockers: [],
              providerSection: 'LocalServiceHosts:SpeechSynthesisBaseUrl',
              modelId: null,
            },
          ],
          selection: null,
          blockers: [],
          localModelOptions: [
            { modelRef: 'voice-1', displayLabel: 'voice-1', isComplete: true, isActive: true },
          ],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={vi.fn()}
      />
    );

    await user.click(screen.getByRole('option', { name: /voice-1/i }));
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechSynthesis',
      'SpeechSynthesis.LocalTts.Http'
    );
    expect(api.settings.localModels.selectActive).toHaveBeenCalledWith('SpeechSynthesis', 'voice-1');
  });

  it('does not activate blocked providers', async () => {
    vi.clearAllMocks();
    const user = userEvent.setup();
    render(
      <TtsToolbarPanel
        service={{
          serviceId: 'SpeechSynthesis',
          displayName: 'Speech Synthesis',
          kind: 'tts',
          status: 'blocked',
          summary: 'blocked',
          activeProviderId: 'SpeechSynthesis.LocalTts.Http',
          activeProviderLabel: 'Local',
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          providerOptions: [
            {
              providerId: 'SpeechSynthesis.Google.TextToSpeech',
              displayName: 'GoogleGeminiApi',
              providerKind: 'Cloud',
              canActivate: false,
              blockers: ['Voice Name is required.'],
              providerSection: 'GoogleGeminiApi',
              modelId: null,
            },
          ],
          selection: null,
          blockers: [],
          localModelOptions: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={vi.fn()}
      />
    );

    await user.click(screen.getByRole('option', { name: /google/i }));

    expect(api.settings.services.updateActiveProvider).not.toHaveBeenCalled();
  });

});
