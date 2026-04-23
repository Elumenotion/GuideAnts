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
  it('switches provider and installed model', async () => {
    const user = userEvent.setup();
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
          localRuntimeOn: true,
          providerOptions: [
            {
              providerId: 'SpeechTranscription.LocalAsr.Http',
              displayName: 'Local',
              providerKind: 'local',
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

    await user.click(screen.getByRole('option', { name: /Local \(local\)/i }));
    await user.click(screen.getByRole('option', { name: /asr-1/i }));
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechTranscription',
      'SpeechTranscription.LocalAsr.Http'
    );
    expect(api.settings.localModels.selectActive).toHaveBeenCalledWith('SpeechTranscription', 'asr-1');
  });
});
