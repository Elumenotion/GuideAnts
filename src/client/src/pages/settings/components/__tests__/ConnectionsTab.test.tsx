import React from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
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

describe('ConnectionsTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSchema).mockResolvedValue(schema as never);
    vi.mocked(api.settings.getOverview).mockResolvedValue({
      serviceModeReadiness: [],
      chatTargets: { ready: 0, total: 0, targets: [] },
      providerConnectionIssues: [],
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

  it('loads schema, selects a section, and saves edits', async () => {
    const user = userEvent.setup();
    const { props } = renderConnectionsTab();

    await waitFor(() => expect(api.settings.getSection).toHaveBeenCalledWith('OpenAI'));

    const organizationInput = await screen.findByDisplayValue('org-1');
    await user.clear(organizationInput);
    await user.type(organizationInput, 'org-updated');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(api.settings.updateSection).toHaveBeenCalledWith(
        'OpenAI',
        expect.objectContaining({
          rowVersion: 'row-1',
          payload: expect.objectContaining({ Organization: 'org-updated' }),
        }),
      );
    });
    expect(props.onRefreshSectionSummaries).toHaveBeenCalled();
  });

  it('surfaces section load failures', async () => {
    vi.mocked(api.settings.getSection).mockRejectedValue(new Error('Section unavailable'));
    renderConnectionsTab();

    expect(await screen.findByText('Section unavailable')).toBeInTheDocument();
  });

  it('shows section summary refresh errors', () => {
    renderConnectionsTab({ sectionSummariesError: 'Summaries failed' });

    expect(screen.getByText(/Summaries failed/i)).toBeInTheDocument();
  });

  it('surfaces schema and usage load failures', async () => {
    vi.mocked(api.settings.getSchema).mockRejectedValue(new Error('Schema unavailable'));
    vi.mocked(api.settings.connections.getUsage).mockRejectedValue(new Error('Usage unavailable'));
    renderConnectionsTab();

    expect(await screen.findByText('Schema unavailable')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByText('Usage unavailable')).toBeInTheDocument();
    });
  });

  it('auto-selects the focused provider section on first render', async () => {
    renderConnectionsTab({ focusedSection: 'OpenAI' });

    await waitFor(() => {
      expect(api.settings.getSection).toHaveBeenCalledWith('OpenAI');
      expect(api.settings.connections.getUsage).toHaveBeenCalledWith('OpenAI');
    });
  });
});
