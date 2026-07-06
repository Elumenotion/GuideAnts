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
  it('switches to local provider when selecting a local model', async () => {
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
          activeProviderId: 'ImageGeneration.Google.Imagen',
          activeProviderLabel: 'Google Gemini',
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          providerOptions: [
            {
              providerId: 'ImageGeneration.Google.Imagen',
              displayName: 'GoogleGeminiApi',
              providerKind: 'Cloud',
              canActivate: true,
              blockers: [],
              providerSection: 'GoogleGeminiApi',
              modelId: 'imagen-3',
            },
            {
              providerId: 'ImageGeneration.LocalSd.Http',
              displayName: 'LocalServiceHosts:ImageGenerationBaseUrl',
              providerKind: 'LocalHttp',
              canActivate: true,
              blockers: [],
              providerSection: 'LocalServiceHosts:ImageGenerationBaseUrl',
              modelId: null,
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
              displayName: 'GoogleGeminiApi',
              providerKind: 'Cloud',
              canActivate: false,
              blockers: ['Missing provider connection value: Google Gemini API Key.'],
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
          localRuntimeOn: false,
          providerOptions: [
            {
              providerId: 'ImageGeneration.Google.Imagen',
              displayName: 'GoogleGeminiApi',
              providerKind: 'Cloud',
              canActivate: true,
              blockers: [],
              providerSection: 'GoogleGeminiApi',
              modelId: 'imagen-3',
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

    await user.click(screen.getByRole('option', { name: /google/i }));
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'ImageGeneration',
      'ImageGeneration.Google.Imagen'
    );
    expect(onRefresh).toHaveBeenCalled();
  });

  it('shows blockers and hides workspace copy when requested', () => {
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
          providerOptions: [],
          selection: null,
          blockers: ['Image endpoint missing'],
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
        showWorkspaceCopy={false}
      />
    );

    expect(screen.getByText('Image endpoint missing')).toBeInTheDocument();
    expect(screen.queryByText(/Workspace controls apply/i)).not.toBeInTheDocument();
  });
});
