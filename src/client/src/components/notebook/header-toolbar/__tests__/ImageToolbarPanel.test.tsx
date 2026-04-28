import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ImageToolbarPanel } from '../ImageToolbarPanel';
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

describe('ImageToolbarPanel', () => {
  it('switches provider and model through settings APIs', async () => {
    const user = userEvent.setup();
    const onRefresh = vi.fn(async () => {});
    render(
      <ImageToolbarPanel
        service={{
          serviceId: 'ImageGeneration',
          displayName: 'Image Generation',
          kind: 'image',
          status: 'ready',
          summary: 'ready',
          activeProviderId: 'ImageGeneration.LocalSd.Http',
          activeProviderLabel: 'Local',
          supportsLocalRuntimePower: true,
          localRuntimeOn: true,
          providerOptions: [
            {
              providerId: 'ImageGeneration.LocalSd.Http',
              displayName: 'Local',
              providerKind: 'local',
              canActivate: true,
              blockers: [],
            },
          ],
          selection: null,
          blockers: [],
          localModelOptions: [
            { modelRef: 'bundle-a', displayLabel: 'bundle-a', isComplete: true, isActive: true },
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

    await user.click(screen.getByRole('option', { name: /Local \(local\)/i }));
    await user.click(screen.getByRole('option', { name: /bundle-a/i }));
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'ImageGeneration',
      'ImageGeneration.LocalSd.Http'
    );
    expect(api.settings.localModels.selectActive).toHaveBeenCalledWith('ImageGeneration', 'bundle-a');
  });

  it('does not activate blocked providers', async () => {
    vi.clearAllMocks();
    const user = userEvent.setup();
    render(
      <ImageToolbarPanel
        service={{
          serviceId: 'ImageGeneration',
          displayName: 'Image Generation',
          kind: 'image',
          status: 'blocked',
          summary: 'blocked',
          activeProviderId: 'ImageGeneration.LocalSd.Http',
          activeProviderLabel: 'Local',
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          providerOptions: [
            {
              providerId: 'ImageGeneration.Google.Imagen',
              displayName: 'Google',
              providerKind: 'Cloud',
              canActivate: false,
              blockers: ['Missing provider connection value: Google Gemini API Key.'],
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

    await user.click(screen.getByRole('option', { name: /Google \(Cloud\)/i }));

    expect(api.settings.services.updateActiveProvider).not.toHaveBeenCalled();
  });
});
