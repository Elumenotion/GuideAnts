import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import '@testing-library/jest-dom';
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
        remove: vi.fn(async () => undefined),
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

  it('switches cloud provider when selecting an available cloud option', async () => {
    const user = userEvent.setup();
    const onRefresh = vi.fn(async () => {});
    render(
      <TtsToolbarPanel
        service={{
          serviceId: 'SpeechSynthesis',
          displayName: 'Speech Synthesis',
          kind: 'tts',
          status: 'ready',
          summary: 'ready',
          activeProviderId: 'SpeechSynthesis.LocalTts.Http',
          activeProviderLabel: 'Local',
          supportsLocalRuntimePower: true,
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
        onRefresh={onRefresh}
        onOpenSettings={vi.fn()}
      />
    );

    await user.click(screen.getByRole('option', { name: /google/i }));
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechSynthesis',
      'SpeechSynthesis.Google.TextToSpeech'
    );
    expect(onRefresh).toHaveBeenCalled();
  });

  it('disables incomplete local model options', () => {
    render(
      <TtsToolbarPanel
        service={{
          serviceId: 'SpeechSynthesis',
          displayName: 'Speech Synthesis',
          kind: 'tts',
          status: 'ready',
          summary: 'ready',
          activeProviderId: 'SpeechSynthesis.LocalTts.Http',
          activeProviderLabel: 'Local',
          supportsLocalRuntimePower: true,
          localRuntimeOn: false,
          providerOptions: [],
          selection: null,
          blockers: [],
          localModelOptions: [
            { modelRef: 'voice-partial', displayLabel: 'voice-partial', isComplete: false, isActive: false },
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

    expect(screen.getByRole('option', { name: /voice-partial/i })).toBeDisabled();
  });

  it('shows loading state and load control for local runtime power', () => {
    render(
      <TtsToolbarPanel
        service={{
          serviceId: 'SpeechSynthesis',
          displayName: 'Speech Synthesis',
          kind: 'tts',
          status: 'inProgress',
          summary: 'loading',
          activeProviderId: 'SpeechSynthesis.LocalTts.Http',
          activeProviderLabel: 'Local',
          supportsLocalRuntimePower: true,
          localRuntimeOn: false,
          providerOptions: [
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
          selection: { resourceId: 'chatterbox', summary: 'chatterbox' },
          blockers: [],
          localModelOptions: [
            { modelRef: 'chatterbox', displayLabel: 'chatterbox', isComplete: true, isActive: true },
          ],
          inProgressOperationId: 'local-runtime',
          inProgressState: 'loading',
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

    expect(screen.getByText('chatterbox — inProgress')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /load selected local local model/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /load selected local local model/i })).toHaveTextContent('Loading...');
  });

  it('shows API errors when selecting a local model fails', async () => {
    vi.clearAllMocks();
    const user = userEvent.setup();
    vi.mocked(api.settings.localModels.selectActive).mockRejectedValueOnce(
      Object.assign(new Error('model_id OmniVoice is not in the curated TTS catalog'), { status: 400 })
    );

    render(
      <TtsToolbarPanel
        service={{
          serviceId: 'SpeechSynthesis',
          displayName: 'Speech Synthesis',
          kind: 'tts',
          status: 'ready',
          summary: 'ready',
          activeProviderId: 'SpeechSynthesis.LocalTts.Http',
          activeProviderLabel: 'Local',
          supportsLocalRuntimePower: true,
          localRuntimeOn: false,
          providerOptions: [
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
            { modelRef: 'OmniVoice', displayLabel: 'OmniVoice', isComplete: true, isActive: false },
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

    await user.click(screen.getByRole('option', { name: /OmniVoice/i }));
    expect(await screen.findByRole('alert')).toHaveTextContent('model_id OmniVoice is not in the curated TTS catalog');
  });

  // ---- T-C4 (F-2/F-1): presence line, last-known list, offline Remove ----

  it('renders the mirror list with a last-known observed stamp and a presence line (F-2)', () => {
    render(
      <TtsToolbarPanel
        service={{
          serviceId: 'SpeechSynthesis',
          displayName: 'Speech Synthesis',
          kind: 'tts',
          status: 'ready',
          summary: 'last known · observed 14:55',
          activeProviderId: 'SpeechSynthesis.LocalTts.Http',
          activeProviderLabel: 'Local TTS',
          supportsLocalRuntimePower: true,
          localRuntimeOn: false,
          providerOptions: [],
          selection: { resourceId: 'chatterbox', summary: 'active: chatterbox' },
          blockers: [],
          localModelOptions: [
            { modelRef: 'chatterbox', displayLabel: 'chatterbox (last known · observed 14:55)', isComplete: true, isActive: true },
          ],
          inProgressOperationId: null,
          inProgressState: null,
          selectionExists: true,
          presence: {
            serviceId: 'SpeechSynthesis',
            baseUrl: 'http://localhost:8110/tts',
            availability: 'Reachable',
            artifactPresent: null,
            selectedRef: null,
            observedUtc: new Date().toISOString(),
            error: null,
          },
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => undefined)}
        onOpenSettings={vi.fn()}
      />,
    );

    expect(screen.getByTestId('presence-line')).toHaveTextContent('endpoint reachable');
    expect(screen.getAllByText(/last known · observed/i).length).toBeGreaterThan(0);
    // No Remove affordance when the selection exists in inventory.
    expect(screen.queryByRole('button', { name: /Remove 'chatterbox'/ })).toBeNull();
  });

  it('selectionExists:false offers Remove and removal succeeds against an absent engine (F-1)', async () => {
    const user = userEvent.setup();
    const onRefresh = vi.fn(async () => undefined);
    render(
      <TtsToolbarPanel
        service={{
          serviceId: 'SpeechSynthesis',
          displayName: 'Speech Synthesis',
          kind: 'tts',
          status: 'requiresLoad',
          summary: 'chatterbox selected. Load the local model to start.',
          activeProviderId: 'SpeechSynthesis.LocalTts.Http',
          activeProviderLabel: 'Local TTS',
          supportsLocalRuntimePower: true,
          localRuntimeOn: false,
          providerOptions: [],
          selection: { resourceId: 'chatterbox', summary: 'active: chatterbox' },
          blockers: [],
          localModelOptions: [], // inventory no longer contains it: a named condition, not a phantom (active)
          inProgressOperationId: null,
          inProgressState: null,
          selectionExists: false,
          presence: {
            serviceId: 'SpeechSynthesis',
            baseUrl: 'http://localhost:8110/tts',
            availability: 'Reachable',
            artifactPresent: false,
            selectedRef: 'chatterbox',
            observedUtc: new Date().toISOString(),
            error: null,
          },
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={onRefresh}
        onOpenSettings={vi.fn()}
      />,
    );

    const remove = screen.getByRole('button', { name: /Remove 'chatterbox' from saved models/ });
    await user.click(remove);
    await waitFor(() => {
      expect(api.settings.localModels.remove).toHaveBeenCalledWith('SpeechSynthesis', 'chatterbox');
      expect(onRefresh).toHaveBeenCalled();
    });
    // No retry loop: the row clears with the single call.
    expect(api.settings.localModels.remove).toHaveBeenCalledTimes(1);
  });

  it('an Unreachable presence renders as a status, never a spinner (F-2)', () => {
    render(
      <TtsToolbarPanel
        service={{
          serviceId: 'SpeechSynthesis',
          displayName: 'Speech Synthesis',
          kind: 'tts',
          status: 'ready',
          summary: 'configured',
          activeProviderId: 'SpeechSynthesis.LocalTts.Http',
          activeProviderLabel: 'Local TTS',
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          providerOptions: [],
          selection: null,
          blockers: [],
          localModelOptions: [],
          inProgressOperationId: null,
          inProgressState: null,
          selectionExists: true,
          presence: {
            serviceId: 'SpeechSynthesis',
            baseUrl: 'http://localhost:8110/tts',
            availability: 'Unreachable',
            artifactPresent: null,
            selectedRef: null,
            observedUtc: new Date().toISOString(),
            error: 'connection refused',
          },
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => undefined)}
        onOpenSettings={vi.fn()}
      />,
    );

    expect(screen.getByTestId('presence-line')).toHaveTextContent('endpoint unreachable');
    expect(screen.getByTestId('presence-line')).toHaveTextContent('connection refused');
  });
});
