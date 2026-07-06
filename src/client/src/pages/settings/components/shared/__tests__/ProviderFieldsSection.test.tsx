import { describe, it, expect, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ProviderEditorStateDto } from '@/types/settings';
import { ProviderFieldsSection } from '../ProviderFieldsSection';

vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      localModels: {
        voicePackOutcome: vi.fn(),
        catalogOutcome: vi.fn(),
        runtimeReadinessOutcome: vi.fn(),
        voicesOutcome: vi.fn(),
      },
    },
  },
}));

// eslint-disable-next-line @typescript-eslint/no-var-requires
import { api } from '../../../../../services/api';

function createProvider(overrides: Partial<ProviderEditorStateDto> = {}): ProviderEditorStateDto {
  return {
    providerId: 'OpenAI.Chat',
    providerKind: 'cloud',
    providerSection: 'OpenAI',
    hasExplicitMode: true,
    isDefaultMode: false,
    connectionConfigured: true,
    connectionMissingFields: [],
    canActivate: true,
    activationBlockers: [],
    fields: {
      ApiKey: { name: 'ApiKey', value: '', isSecret: true, hasValue: true },
      Endpoint: { name: 'Endpoint', value: 'https://api.example.com', isSecret: false, hasValue: true },
      TimeoutSeconds: { name: 'TimeoutSeconds', value: '30', isSecret: false, hasValue: true },
      ModelId: { name: 'ModelId', value: 'gpt-4', isSecret: false, hasValue: true },
    },
    runtimeDependencies: [],
    operativeFields: ['ApiKey', 'Endpoint', 'TimeoutSeconds', 'ModelId'],
    diagnosticFields: [],
    fieldMetadata: [
      { name: 'ApiKey', kind: 'secret', required: true, operative: true },
      { name: 'Endpoint', kind: 'url', required: true, operative: true },
      { name: 'TimeoutSeconds', kind: 'int', required: false, operative: true },
      {
        name: 'ModelId',
        kind: 'enum',
        required: true,
        operative: true,
        enumOptions: ['gpt-4', 'gpt-4o'],
      },
    ],
    ...overrides,
  };
}

describe('ProviderFieldsSection', () => {
  it('renders operative fields and patches values', async () => {
    const user = userEvent.setup();
    const onPatch = vi.fn();
    const onClearFieldError = vi.fn();

    render(
      <ProviderFieldsSection
        provider={createProvider()}
        draft={{}}
        fieldErrors={{}}
        onPatch={onPatch}
        onClearFieldError={onClearFieldError}
      />
    );

    const endpoint = screen.getByDisplayValue('https://api.example.com');
    await user.clear(endpoint);
    await user.type(endpoint, 'https://new.example.com');

    expect(onClearFieldError).toHaveBeenCalled();
    expect(onPatch).toHaveBeenCalled();
  });

  it('shows connection blocked banner when connection is not configured', () => {
    render(
      <ProviderFieldsSection
        provider={createProvider({
          connectionConfigured: false,
          connectionMissingFields: ['ApiKey', 'Endpoint'],
        })}
        draft={{}}
        fieldErrors={{}}
        onPatch={vi.fn()}
      />
    );

    expect(screen.getByText(/Configure this provider connection first/i)).toBeInTheDocument();
    expect(screen.getByText(/ApiKey, Endpoint/)).toBeInTheDocument();
  });

  it('shows explicit mode notice when provider has no explicit mode', () => {
    render(
      <ProviderFieldsSection
        provider={createProvider({ hasExplicitMode: false })}
        draft={{}}
        fieldErrors={{}}
        onPatch={vi.fn()}
      />
    );

    expect(screen.getByText(/create an explicit service mode/i)).toBeInTheDocument();
  });

  it('shows field validation errors', () => {
    render(
      <ProviderFieldsSection
        provider={createProvider({ operativeFields: ['Endpoint'] })}
        draft={{}}
        fieldErrors={{ Endpoint: 'Endpoint is required' }}
        onPatch={vi.fn()}
      />
    );

    expect(screen.getByRole('alert')).toHaveTextContent('Endpoint is required');
  });

  it('shows empty state when no operative fields are editable', () => {
    render(
      <ProviderFieldsSection
        provider={createProvider({ operativeFields: [], fieldMetadata: [] })}
        draft={{}}
        fieldErrors={{}}
        onPatch={vi.fn()}
      />
    );

    expect(screen.getByText(/No service-mode fields are editable/i)).toBeInTheDocument();
  });

  it('renders secret field with stored value hint', () => {
    render(
      <ProviderFieldsSection
        provider={createProvider({ operativeFields: ['ApiKey'] })}
        draft={{}}
        fieldErrors={{}}
        onPatch={vi.fn()}
      />
    );

    expect(screen.getByText(/credential is already saved/i)).toBeInTheDocument();
  });

  it('patches int and enum field values', async () => {
    const user = userEvent.setup();
    const onPatch = vi.fn();

    render(
      <ProviderFieldsSection
        provider={createProvider({ operativeFields: ['TimeoutSeconds', 'ModelId'] })}
        draft={{}}
        fieldErrors={{}}
        onPatch={onPatch}
      />
    );

    await user.clear(screen.getByRole('spinbutton'));
    await user.type(screen.getByRole('spinbutton'), '45');
    expect(onPatch).toHaveBeenCalled();

    await user.selectOptions(screen.getByDisplayValue('gpt-4'), 'gpt-4o');
    expect(onPatch).toHaveBeenCalledWith({ ModelId: 'gpt-4o' });
  });

  it('renders voice-pack picker for local TTS reference voice', async () => {
    const user = userEvent.setup();
    const onPatch = vi.fn();
    (api.settings.localModels.catalogOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        entries: [{ id: 'chatterbox', voiceInput: 'voice_pack' }],
      },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { catalogEntryId: 'chatterbox' },
    });
    (api.settings.localModels.voicePackOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        referenceText: 'This is the reference transcript for voices',
        voices: [
          { voiceId: 'af_alloy', displayName: 'af alloy' },
          { voiceId: 'am_adam', displayName: 'am adam' },
        ],
      },
    });

    render(
      <ProviderFieldsSection
        provider={createProvider({
          providerId: 'SpeechSynthesis.LocalTts.Http',
          operativeFields: ['VoiceName'],
          fields: {
            VoiceName: { name: 'VoiceName', value: 'af_alloy', isSecret: false, hasValue: true },
          },
          fieldMetadata: [{ name: 'VoiceName', kind: 'text', required: false, operative: true }],
        })}
        draft={{}}
        fieldErrors={{}}
        onPatch={onPatch}
      />
    );

    expect(screen.getByText('Reference voice')).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByRole('option', { name: /am_adam — am adam/i })).toBeInTheDocument();
    });

    await user.selectOptions(screen.getByRole('combobox', { name: /Reference voice/i }), 'am_adam');
    expect(onPatch).toHaveBeenCalledWith({ VoiceName: 'am_adam' });
  });

  it('renders default text field with error styling', async () => {
    const user = userEvent.setup();
    const onPatch = vi.fn();
    const provider = createProvider({
      operativeFields: ['CustomField'],
      fields: {
        CustomField: { name: 'CustomField', value: 'hello', isSecret: false, hasValue: true },
      },
      fieldMetadata: [{ name: 'CustomField', kind: 'text', required: false, operative: true }],
    });

    render(
      <ProviderFieldsSection
        provider={provider}
        draft={{}}
        fieldErrors={{ CustomField: 'Invalid value' }}
        onPatch={onPatch}
      />
    );

    const input = screen.getByDisplayValue('hello');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    await user.type(input, '!');
    expect(onPatch).toHaveBeenCalled();
  });
});
