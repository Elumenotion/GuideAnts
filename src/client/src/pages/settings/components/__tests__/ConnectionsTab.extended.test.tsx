import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ToastProvider } from '../../../../components/common/Toast';
import { ConnectionsTab } from '../ConnectionsTab';
import { api } from '../../../../services/api';
import type { SettingsSectionSummaryDto } from '../../../../types/settings';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      getSchema: vi.fn(),
      getOverview: vi.fn(),
      getSection: vi.fn(),
      updateSection: vi.fn(),
      connections: {
        getUsage: vi.fn(),
      },
    },
  },
}));

const openAiSummary: SettingsSectionSummaryDto = {
  sectionName: 'OpenAI',
  hasSecrets: true,
  readinessStatus: 'configured',
  missingFields: [],
};

const schema = {
  sections: [
    {
      sectionName: 'OpenAI',
      schemaVersion: 1,
      hasSecrets: true,
      properties: [
        {
          name: 'ApiKey',
          valueType: 'string',
          isSecret: true,
          isEditable: true,
          isRequired: true,
        },
        {
          name: 'Organization',
          valueType: 'string',
          isSecret: false,
          isEditable: true,
          isRequired: false,
        },
      ],
    },
  ],
  services: [],
  providers: [],
  runtimeDependencies: [],
};

const sectionData = {
  sectionName: 'OpenAI',
  schemaVersion: 1,
  rowVersion: 'row-1',
  updatedUtc: '2026-01-01T00:00:00Z',
  payload: { Organization: 'org-1' },
  secretHasValue: { ApiKey: true },
};

function renderConnectionsTab(overrides: Partial<React.ComponentProps<typeof ConnectionsTab>> = {}) {
  const props = {
    focusedSection: 'OpenAI',
    providerSections: [openAiSummary],
    sectionSummariesError: null,
    onRefreshSectionSummaries: vi.fn(),
    onOpenServiceConsumer: vi.fn(),
    onOpenChatTarget: vi.fn(),
    ...overrides,
  };

  return {
    props,
    ...render(
      <ToastProvider>
        <ConnectionsTab {...props} />
      </ToastProvider>,
    ),
  };
}

describe('ConnectionsTab extended', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSchema).mockResolvedValue(schema as never);
    vi.mocked(api.settings.getOverview).mockResolvedValue({
      serviceModeReadiness: [],
      chatTargets: { ready: 0, total: 0, targets: [] },
    } as never);
    vi.mocked(api.settings.getSection).mockResolvedValue(sectionData as never);
    vi.mocked(api.settings.connections.getUsage).mockResolvedValue({
      section: 'OpenAI',
      modes: [],
      chatTargets: [],
    } as never);
    vi.mocked(api.settings.updateSection).mockResolvedValue({
      ...sectionData,
      rowVersion: 'row-2',
      payload: { Organization: 'org-updated' },
    } as never);
  });

  it('resets unsaved draft edits back to the loaded section payload', async () => {
    const user = userEvent.setup();
    renderConnectionsTab();

    const organizationInput = await screen.findByDisplayValue('org-1');
    await user.clear(organizationInput);
    await user.type(organizationInput, 'draft-value');
    expect(screen.getByDisplayValue('draft-value')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Reset' }));

    expect(await screen.findByDisplayValue('org-1')).toBeInTheDocument();
  });

  it('preserves drafts on version conflicts and supports discard or reapply', async () => {
    const user = userEvent.setup();
    const conflict = Object.assign(new Error('Conflict'), { status: 409 });
    vi.mocked(api.settings.updateSection).mockRejectedValue(conflict);
    vi.mocked(api.settings.getSection)
      .mockResolvedValueOnce(sectionData as never)
      .mockResolvedValueOnce({
        ...sectionData,
        rowVersion: 'row-2',
        payload: { Organization: 'server-value' },
      } as never);

    renderConnectionsTab();

    const organizationInput = await screen.findByDisplayValue('org-1');
    await user.clear(organizationInput);
    await user.type(organizationInput, 'local-draft');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(
      await screen.findByText(/Another update was saved first/i),
    ).toBeInTheDocument();
    expect(await screen.findByDisplayValue('server-value')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Reapply' }));
    expect(await screen.findByDisplayValue('local-draft')).toBeInTheDocument();
    expect(screen.queryByText(/Another update was saved first/i)).not.toBeInTheDocument();
  });

  it('renders usage chips and routes service and chat target navigation', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.connections.getUsage).mockResolvedValue({
      section: 'OpenAI',
      modes: [
        {
          service: 'Embeddings',
          modeId: 'default',
          isDefault: true,
        },
      ],
      chatTargets: [{ modelId: 'gpt-test', provider: 'openai-chat' }],
    } as never);

    const { props } = renderConnectionsTab();

    const serviceChip = await screen.findByRole('button', { name: /Embeddings/i });
    await user.click(serviceChip);
    expect(props.onOpenServiceConsumer).toHaveBeenCalledWith('Embeddings');

    await user.click(screen.getByRole('button', { name: /gpt-test/i }));
    expect(props.onOpenChatTarget).toHaveBeenCalledWith('gpt-test');
  });

  it('shows an empty-state message when no consumers reference the section', async () => {
    renderConnectionsTab();

    expect(
      await screen.findByText(/No service mappings or active assistant-referenced models/i),
    ).toBeInTheDocument();
  });

  it('surfaces non-conflict save failures', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.updateSection).mockRejectedValue(new Error('Save failed'));

    renderConnectionsTab();

    const organizationInput = await screen.findByDisplayValue('org-1');
    await user.clear(organizationInput);
    await user.type(organizationInput, 'broken-save');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Save failed')).toBeInTheDocument();
  });
});
