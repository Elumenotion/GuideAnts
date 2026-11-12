import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ToastProvider } from '../../../../components/common/Toast';
import { OverviewTab } from '../OverviewTab';
import { api } from '../../../../services/api';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      getOverview: vi.fn(),
      chatDefaults: {
        get: vi.fn(),
        update: vi.fn(),
      },
      services: {
        get: vi.fn(),
      },
    },
  },
}));

vi.mock('../../../../components/chat-model/ChatModelConfigurator', () => ({
  ChatModelConfigurator: ({
    onChange,
  }: {
    onChange: (value: {
      modelId: string;
      temperature: number | null;
      topP: number | null;
      reasoningEffort?: string;
      samplingOverrides: Record<string, unknown>;
    }) => void;
  }) => (
    <button
      type="button"
      onClick={() =>
        onChange({
          modelId: 'gpt-test',
          temperature: 0.4,
          topP: null,
          reasoningEffort: undefined,
          samplingOverrides: {},
        })
      }
    >
      set-default-model
    </button>
  ),
}));

const overview = {
  providerIssues: [],
  chatTargets: {
    targets: [{ modelId: 'gpt-test', provider: 'openai-chat', status: 'ready' }],
  },
  serviceModeReadiness: [
    { service: 'Embeddings', ready: 1, total: 1 },
    { service: 'ImageGeneration', ready: 0, total: 1 },
    { service: 'SpeechSynthesis', ready: 1, total: 1 },
    { service: 'SpeechTranscription', ready: 1, total: 1 },
    { service: 'DocumentIntelligence', ready: 1, total: 1 },
  ],
};

const chatDefaults = {
  modelId: 'gpt-test',
  temperature: 0.7,
  topP: null,
  reasoningEffort: undefined,
  overrideAllChatModels: false,
  samplingOverrides: {},
};

function renderOverviewTab() {
  return render(
    <ToastProvider>
      <OverviewTab
        onOpenConnections={vi.fn()}
        onOpenServices={vi.fn()}
        onOpenModelsRuntime={vi.fn()}
        catalogVersion={1}
      />
    </ToastProvider>,
  );
}

describe('OverviewTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getOverview).mockResolvedValue(overview as never);
    vi.mocked(api.settings.chatDefaults.get).mockResolvedValue(chatDefaults as never);
    vi.mocked(api.settings.chatDefaults.update).mockResolvedValue({
      ...chatDefaults,
      overrideAllChatModels: true,
    } as never);
    vi.mocked(api.settings.services.get).mockResolvedValue({
      serviceId: 'Embeddings',
      activeProviderId: 'Embeddings.Local',
      providers: [],
      readiness: { status: 'ready', blockers: [], warnings: [] },
    } as never);
  });

  it('loads overview data and saves default chat model settings', async () => {
    const user = userEvent.setup();
    renderOverviewTab();

    expect(await screen.findByText('Overview')).toBeInTheDocument();
    expect(await screen.findByText('Default Chat Model')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'set-default-model' }));
    await user.click(screen.getByRole('checkbox', { name: /Override all chat models/i }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(api.settings.chatDefaults.update).toHaveBeenCalled();
    });
    expect(await screen.findByText(/Override is on/i)).toBeInTheDocument();
  });

  it('deep-links to connections and services', async () => {
    const user = userEvent.setup();
    const onOpenConnections = vi.fn();
    const onOpenServices = vi.fn();

    render(
      <ToastProvider>
        <OverviewTab
          onOpenConnections={onOpenConnections}
          onOpenServices={onOpenServices}
          onOpenModelsRuntime={vi.fn()}
        />
      </ToastProvider>,
    );

    await screen.findByText('Chat providers');
    await user.click(screen.getAllByRole('button', { name: 'Open' })[0]);
    expect(onOpenConnections).toHaveBeenCalled();

    const embeddingsRow = screen.getAllByText('Embeddings')[0].closest('li');
    await user.click(within(embeddingsRow!).getByRole('button', { name: 'Open' }));
    expect(onOpenServices).toHaveBeenCalledWith('Embeddings');
  });

  // ---- T-C1 additions: offline regression locks (R1 / F-2) ----

  it('renders the configurator and Save path when getOverview never resolves — no full-tab spinner (R1)', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.getOverview).mockImplementation(
      () => new Promise(() => undefined), // never resolves: the hostile case
    );

    renderOverviewTab();

    // The chat configurator is present and usable even while overview data is absent.
    expect(await screen.findByText('Default Chat Model')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'set-default-model' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'set-default-model' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => {
      expect(api.settings.chatDefaults.update).toHaveBeenCalled();
    });

    // No full-tab blocking spinner: the tab never replaces its content with a spinner.
    const spinnerRoles = screen.queryAllByRole('status');
    expect(spinnerRoles.length).toBeLessThanOrEqual(1);
  });

  it('readiness rows render a status with observed state; Unknown renders as "not yet observed" (F-2)', async () => {
    vi.mocked(api.settings.getOverview).mockResolvedValue({
      ...overview,
      llamaRuntime: {
        loadedAliases: 0,
        totalAliases: 0,
        missingArtifactAliases: [],
        inventorySource: 'None',
        observedUtc: null,
      },
    } as never);

    renderOverviewTab();

    await screen.findByText('Overview');
    // The runtime section states its never-observed truth instead of a blank or a spinner.
    const cell = await screen.findByText(/not yet observed/i);
    expect(cell).toBeInTheDocument();
    expect(screen.queryByText(/checking…/i)).toBeNull();
  });

  it('service row failure shows per-row unavailable, not a blank tab (loadServiceRows)', async () => {
    vi.mocked(api.settings.services.get).mockRejectedValue(new Error('service row down'));

    renderOverviewTab();

    await screen.findByText('Overview');
    // The Embeddings row reports unavailable inline; the rest of the tab still renders.
    await waitFor(() => {
      expect(screen.getAllByText(/unavailable/i).length).toBeGreaterThan(0);
    });
    expect(screen.getByText('Default Chat Model')).toBeInTheDocument();
  });

  it('shows overview load failures with retry', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.getOverview)
      .mockRejectedValueOnce(new Error('Overview unavailable'))
      .mockResolvedValueOnce(overview as never);

    renderOverviewTab();

    expect(await screen.findByText(/Overview unavailable/i)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => {
      expect(api.settings.getOverview).toHaveBeenCalledTimes(2);
    });
  });
});
