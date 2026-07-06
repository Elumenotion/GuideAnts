import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { AsrToolbarPanel } from '../AsrToolbarPanel';
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

describe('AsrToolbarPanel', () => {
  it('switches to local provider when selecting an installed model', async () => {
    const user = userEvent.setup();
    render(
      <AsrToolbarPanel
        service={{
          serviceId: 'SpeechTranscription',
          displayName: 'Speech Transcription',
          kind: 'asr',
          status: 'ready',
          summary: 'ready',
          activeProviderId: 'SpeechTranscription.Google.SpeechToText',
          activeProviderLabel: 'Google Gemini',
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          providerOptions: [
            {
              providerId: 'SpeechTranscription.Google.SpeechToText',
              displayName: 'GoogleGeminiApi',
              providerKind: 'Cloud',
              canActivate: true,
              blockers: [],
              providerSection: 'GoogleGeminiApi',
              modelId: 'gemini-2.5-flash',
            },
            {
              providerId: 'SpeechTranscription.LocalAsr.Http',
              displayName: 'LocalServiceHosts:SpeechTranscriptionBaseUrl',
              providerKind: 'LocalHttp',
              canActivate: true,
              blockers: [],
              providerSection: 'LocalServiceHosts:SpeechTranscriptionBaseUrl',
              modelId: null,
            },
          ],
          selection: null,
          blockers: [],
          localModelOptions: [
            { modelRef: 'asr-1', displayLabel: 'asr-1', isComplete: true, isActive: true },
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

    await user.click(screen.getByRole('option', { name: /asr-1/i }));
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechTranscription',
      'SpeechTranscription.LocalAsr.Http'
    );
    expect(api.settings.localModels.selectActive).toHaveBeenCalledWith('SpeechTranscription', 'asr-1');
  });

  it('does not activate blocked providers', async () => {
    vi.clearAllMocks();
    const user = userEvent.setup();
    render(
      <AsrToolbarPanel
        service={{
          serviceId: 'SpeechTranscription',
          displayName: 'Speech Transcription',
          kind: 'asr',
          status: 'blocked',
          summary: 'blocked',
          activeProviderId: 'SpeechTranscription.LocalAsr.Http',
          activeProviderLabel: 'Local',
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          providerOptions: [
            {
              providerId: 'SpeechTranscription.Google.SpeechToText',
              displayName: 'GoogleGeminiApi',
              providerKind: 'Cloud',
              canActivate: false,
              blockers: ['Transcription Model ID is required.'],
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

  it('switches cloud provider when selecting an available cloud option', async () => {
    const user = userEvent.setup();
    const onRefresh = vi.fn(async () => {});
    render(
      <AsrToolbarPanel
        service={{
          serviceId: 'SpeechTranscription',
          displayName: 'Speech Transcription',
          kind: 'asr',
          status: 'ready',
          summary: 'ready',
          activeProviderId: 'SpeechTranscription.LocalAsr.Http',
          activeProviderLabel: 'Local',
          supportsLocalRuntimePower: true,
          localRuntimeOn: false,
          providerOptions: [
            {
              providerId: 'SpeechTranscription.Google.SpeechToText',
              displayName: 'GoogleGeminiApi',
              providerKind: 'Cloud',
              canActivate: true,
              blockers: [],
              providerSection: 'GoogleGeminiApi',
              modelId: 'gemini-2.5-flash',
            },
            {
              providerId: 'SpeechTranscription.LocalAsr.Http',
              displayName: 'LocalServiceHosts:SpeechTranscriptionBaseUrl',
              providerKind: 'LocalHttp',
              canActivate: true,
              blockers: [],
              providerSection: 'LocalServiceHosts:SpeechTranscriptionBaseUrl',
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
        onRefresh={onRefresh}
        onOpenSettings={vi.fn()}
      />
    );

    await user.click(screen.getByRole('option', { name: /google/i }));
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechTranscription',
      'SpeechTranscription.Google.SpeechToText'
    );
    expect(onRefresh).toHaveBeenCalled();
  });

  it('renders service blockers and opens settings', async () => {
    const user = userEvent.setup();
    const onOpenSettings = vi.fn();
    render(
      <AsrToolbarPanel
        service={{
          serviceId: 'SpeechTranscription',
          displayName: 'Speech Transcription',
          kind: 'asr',
          status: 'blocked',
          summary: 'blocked',
          activeProviderId: 'SpeechTranscription.LocalAsr.Http',
          activeProviderLabel: 'Local',
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          providerOptions: [],
          selection: null,
          blockers: ['ASR endpoint missing'],
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
        onOpenSettings={onOpenSettings}
      />
    );

    expect(screen.getByText('ASR endpoint missing')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /open in settings/i }));
    expect(onOpenSettings).toHaveBeenCalled();
  });
});
