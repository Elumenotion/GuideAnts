import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import type { ProviderEditorStateDto, ServiceEditorStateDto } from '../../../../../types/settings';
import { ServiceEditorBase } from '../ServiceEditorBase';

const mockSave = vi.fn(async () => true);
const mockSwitchProvider = vi.fn();
const mockPatchActiveDraft = vi.fn();
const mockClearFieldError = vi.fn();

function makeProvider(overrides: Partial<ProviderEditorStateDto> = {}): ProviderEditorStateDto {
  return {
    providerId: 'SpeechSynthesis.Local.Tts',
    providerKind: 'Local',
    providerSection: 'LocalTts',
    hasExplicitMode: true,
    isDefaultMode: true,
    connectionConfigured: true,
    connectionMissingFields: [],
    canActivate: true,
    activationBlockers: [],
    fields: {
      Endpoint: { name: 'Endpoint', value: 'http://localhost:8110', isSecret: false, hasValue: true },
    },
    runtimeDependencies: [
      { key: 'HF_TOKEN', hasValue: true, currentValue: 'set' },
    ],
    operativeFields: ['Endpoint'],
    diagnosticFields: [],
    fieldMetadata: [
      {
        name: 'Endpoint',
        kind: 'url',
        required: true,
        enumOptions: null,
        operative: true,
      },
    ],
    ...overrides,
  };
}

function makeControllerValue(
  overrides: Partial<ReturnType<typeof buildController>> = {},
) {
  return { ...buildController(), ...overrides };
}

function buildController() {
  const provider = makeProvider();
  const state: ServiceEditorStateDto = {
    serviceId: 'SpeechSynthesis',
    activeProviderId: provider.providerId,
    providers: [provider],
    readiness: { status: 'ready', blockers: [], warnings: [] },
  };

  return {
    state,
    loading: false,
    error: null,
    saving: false,
    fieldErrors: {},
    draft: {
      activeProviderId: provider.providerId,
      activeDraft: {},
      switchProvider: mockSwitchProvider,
      patchActiveDraft: mockPatchActiveDraft,
    },
    selectedProvider: provider,
    persistedActiveLabel: 'Local TTS',
    editingProviderLabel: null,
    providerOptions: [
      {
        providerId: provider.providerId,
        displayName: 'Local TTS',
        kind: 'Local',
        hasExplicitMode: true,
        connectionConfigured: true,
        canActivate: true,
        blocker: null,
      },
    ],
    save: mockSave,
    clearFieldError: mockClearFieldError,
    load: vi.fn(async () => {}),
  };
}

vi.mock('../../../state/useServiceEditorController', () => ({
  useServiceEditorController: vi.fn(() => makeControllerValue()),
}));

import { useServiceEditorController } from '../../../state/useServiceEditorController';

describe('ServiceEditorBase', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(useServiceEditorController).mockReturnValue(makeControllerValue() as ReturnType<typeof useServiceEditorController>);
  });

  it('shows loading state while controller is loading', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeControllerValue(),
      loading: true,
      state: null,
      selectedProvider: undefined,
    } as ReturnType<typeof useServiceEditorController>);

    render(<ServiceEditorBase serviceId="SpeechSynthesis" title="Speech Synthesis" />);
    expect(screen.getByText(/Loading Speech Synthesis settings/i)).toBeInTheDocument();
  });

  it('shows error state when service state is unavailable', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeControllerValue(),
      state: null,
      selectedProvider: undefined,
      error: 'Service missing',
    } as ReturnType<typeof useServiceEditorController>);

    render(<ServiceEditorBase serviceId="SpeechSynthesis" title="Speech Synthesis" />);
    expect(screen.getByText('Service missing')).toBeInTheDocument();
  });

  it('renders shell with provider settings, dependencies, and save action', () => {
    render(
      <ServiceEditorBase
        serviceId="SpeechSynthesis"
        title="Speech Synthesis"
        serviceSettings={<div data-testid="service-settings">Extra settings</div>}
        extraActions={<button type="button">Extra</button>}
      />,
    );

    expect(screen.getByRole('heading', { name: 'Speech Synthesis' })).toBeInTheDocument();
    expect(screen.getByText(/Active provider: Local TTS/)).toBeInTheDocument();
    expect(screen.getByText('Operational Dependencies')).toBeInTheDocument();
    expect(screen.getByText('HF_TOKEN')).toBeInTheDocument();
    expect(screen.getByTestId('service-settings')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Extra' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
  });

  it('renders provider extra slots as functions with selected provider', () => {
    render(
      <ServiceEditorBase
        serviceId="SpeechSynthesis"
        title="Speech Synthesis"
        providerExtraTop={(provider) => <div data-testid="top-extra">{provider.providerId}</div>}
        providerExtra={(provider) => <div data-testid="bottom-extra">{provider.providerKind}</div>}
      />,
    );

    expect(screen.getByTestId('top-extra')).toHaveTextContent('SpeechSynthesis.Local.Tts');
    expect(screen.getByTestId('bottom-extra')).toHaveTextContent('Local');
  });

  it('disables save when provider connection is not configured', () => {
    const disconnected = makeProvider({
      connectionConfigured: false,
      connectionMissingFields: ['Endpoint'],
      operativeFields: ['TimeoutSeconds'],
    });
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeControllerValue(),
      selectedProvider: disconnected,
      state: {
        serviceId: 'SpeechSynthesis',
        activeProviderId: disconnected.providerId,
        providers: [disconnected],
        readiness: { status: 'blocked', blockers: ['Endpoint missing'], warnings: [] },
      },
    } as ReturnType<typeof useServiceEditorController>);

    render(<ServiceEditorBase serviceId="SpeechSynthesis" title="Speech Synthesis" />);
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('enables save when Foundry connection fields are editable inline', () => {
    const foundry = makeProvider({
      providerId: 'SpeechSynthesis.AzureSpeech.Ssml',
      connectionConfigured: false,
      connectionMissingFields: ['ApiKey', 'Region'],
      operativeFields: ['Endpoint', 'ApiKey', 'Region', 'TimeoutSeconds'],
      relatedChatConnectionConfigured: true,
    });
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeControllerValue(),
      selectedProvider: foundry,
      state: {
        serviceId: 'SpeechSynthesis',
        activeProviderId: foundry.providerId,
        providers: [foundry],
        readiness: { status: 'blocked', blockers: ['ApiKey missing'], warnings: [] },
      },
    } as ReturnType<typeof useServiceEditorController>);

    render(<ServiceEditorBase serviceId="SpeechSynthesis" title="Speech Synthesis" />);
    const saveButton = screen.getByRole('button', { name: 'Save' });
    expect(saveButton).not.toBeDisabled();
    expect(saveButton).toHaveAttribute(
      'title',
      'Save will write connection details and activate provider.'
    );
  });

  it('invokes save when save button is clicked', () => {
    render(<ServiceEditorBase serviceId="SpeechSynthesis" title="Speech Synthesis" />);
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(mockSave).toHaveBeenCalled();
  });

  it('switches provider when another option is selected', () => {
    const altProvider = makeProvider({
      providerId: 'SpeechSynthesis.Cloud',
      providerSection: 'CloudTts',
    });
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeControllerValue(),
      providerOptions: [
        {
          providerId: 'SpeechSynthesis.Local.Tts',
          displayName: 'Local TTS',
          kind: 'Local',
          hasExplicitMode: true,
          connectionConfigured: true,
          canActivate: true,
          blocker: null,
        },
        {
          providerId: 'SpeechSynthesis.Cloud',
          displayName: 'Cloud TTS',
          kind: 'Cloud',
          hasExplicitMode: false,
          connectionConfigured: true,
          canActivate: true,
          blocker: null,
        },
      ],
      state: {
        serviceId: 'SpeechSynthesis',
        activeProviderId: altProvider.providerId,
        providers: [makeProvider(), altProvider],
        readiness: { status: 'ready', blockers: [], warnings: [] },
      },
    } as ReturnType<typeof useServiceEditorController>);

    render(<ServiceEditorBase serviceId="SpeechSynthesis" title="Speech Synthesis" />);
    fireEvent.click(screen.getByRole('button', { name: /Cloud TTS/i }));
    expect(mockSwitchProvider).toHaveBeenCalledWith('SpeechSynthesis.Cloud');
  });

  it('shows explicit-mode save hint when provider has no explicit mode', () => {
    const implicit = makeProvider({ hasExplicitMode: false });
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeControllerValue(),
      selectedProvider: implicit,
      state: {
        serviceId: 'SpeechSynthesis',
        activeProviderId: implicit.providerId,
        providers: [implicit],
        readiness: { status: 'ready', blockers: [], warnings: [] },
      },
    } as ReturnType<typeof useServiceEditorController>);

    render(<ServiceEditorBase serviceId="SpeechSynthesis" title="Speech Synthesis" />);
    expect(screen.getByRole('button', { name: 'Save' })).toHaveAttribute(
      'title',
      'Save will create an explicit service mode and activate provider.'
    );
  });

  it('shows inline error from controller next to actions', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeControllerValue(),
      error: 'Save failed',
    } as ReturnType<typeof useServiceEditorController>);

    render(<ServiceEditorBase serviceId="SpeechSynthesis" title="Speech Synthesis" />);
    expect(screen.getByText('Save failed')).toBeInTheDocument();
  });
});
