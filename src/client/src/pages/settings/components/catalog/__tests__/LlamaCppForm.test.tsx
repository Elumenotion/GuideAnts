import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { LLAMA_MMPROJ_ROLE_ID, LLAMA_MODEL_ROLE_ID } from '../../../editors/common';
import { createEmptyAddModelWizardState, createEmptyProfileForm } from '../../../utils';
import { LlamaCppAddForm, LlamaCppEditForm } from '../providers/LlamaCppForm';
import type { ProviderAddForm } from '../providers/types';
import type { CatalogEditState } from '../../../types';
import type { LlamaRuntimeInventoryItemDto, SettingsRuntimeProfileDto } from '../../../../../types/settings';

vi.mock('../../../editors/common', async () => {
  const actual = await vi.importActual<typeof import('../../../editors/common')>('../../../editors/common');
  return {
    ...actual,
    RepositoryFilePicker: ({
      repository,
      onRepositoryChange,
      onChange,
    }: {
      repository: string;
      onRepositoryChange: (value: string) => void;
      onChange: (values: Record<string, string>) => void;
    }) => (
      <div data-testid="repo-picker">
        <input
          data-testid="repo-input"
          aria-label="Repository"
          value={repository}
          onChange={(event) => onRepositoryChange(event.target.value)}
        />
        <button
          type="button"
          onClick={() =>
            onChange({
              [LLAMA_MODEL_ROLE_ID]: 'Qwen3-9B-Q5_K_M.gguf',
              [LLAMA_MMPROJ_ROLE_ID]: 'mmproj-F16.gguf',
            })
          }
        >
          Pick files
        </button>
      </div>
    ),
  };
});

function makeProfile(
  overrides: Partial<SettingsRuntimeProfileDto> = {},
): SettingsRuntimeProfileDto {
  return {
    profileId: 'qwen3_5',
    displayName: 'Qwen 3.5',
    description: '',
    combineSystemAndDeveloperMessages: false,
    thoughtBlockPattern: '',
    samplingParametersJson: '{}',
    thinkingControlJson: '{"choiceActions":{"minimal":[],"medium":[]}}',
    providers: ['llama-cpp'],
    created: '2026-01-01T00:00:00Z',
    updated: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function makeAddProps(overrides: Partial<ProviderAddForm> = {}): ProviderAddForm {
  const onChange = vi.fn();
  return {
    value: createEmptyAddModelWizardState('llama-cpp'),
    onChange,
    profiles: [makeProfile()],
    profilesLoading: false,
    inventory: [],
    onCreateRuntimeProfile: vi.fn(async () => {}),
    onCreateCustomRuntimeProfile: vi.fn(async (request) => ({
      ...makeProfile(),
      profileId: request.profileId,
      displayName: request.displayName,
    })),
    ...overrides,
  };
}

function makeEditState(overrides: Partial<CatalogEditState> = {}): CatalogEditState {
  return {
    modelId: 'qwen-local',
    provider: 'llama-cpp',
    displayName: 'Qwen Local',
    description: '',
    displayOrder: '',
    isActive: true,
    runtimeProfileId: 'qwen3_5',
    localRuntimeRouterModelId: 'QwenAlias',
    localRuntimeLoadParamsJson: '',
    localRuntimeParallelToolCalls: false,
    localRuntimeRouterContextSize: '',
    localRuntimeRouterCacheRamMib: '',
    ...overrides,
  };
}

function makeInventoryRow(
  overrides: Partial<LlamaRuntimeInventoryItemDto> = {},
): LlamaRuntimeInventoryItemDto {
  return {
    routerModelId: 'QwenAlias',
    runtimeState: 'loaded',
    hasModelFile: true,
    hasMmprojFile: false,
    modelPath: '/models-local/llama/QwenAlias/model.gguf',
    mmprojPath: null,
    catalogModelIds: ['qwen-local'],
    notebookReferenceCount: 2,
    routerContextSize: 8192,
    routerCacheRamMib: 512,
    ...overrides,
  };
}

describe('LlamaCppAddForm', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders hugging face install source by default with repository picker', () => {
    render(<LlamaCppAddForm {...makeAddProps()} />);

    expect(screen.getByRole('option', { name: 'Install from Hugging Face' })).toBeInTheDocument();
    expect(screen.getByTestId('repo-picker')).toBeInTheDocument();
    expect(screen.getByLabelText('Repository')).toBeInTheDocument();
  });

  it('shows runtime unavailable banner when inventory probe fails', () => {
    render(
      <LlamaCppAddForm
        {...makeAddProps({ inventoryError: 'No local llama server configured for this container' })}
      />,
    );

    expect(screen.getByText(/No local llama server is configured/i)).toBeInTheDocument();
  });

  it('switches to attach existing alias mode and lists attachable aliases', () => {
    const onChange = vi.fn();
    const inventory = [
      makeInventoryRow({
        routerModelId: 'orphan-alias',
        catalogModelIds: [],
        hasModelFile: true,
      }),
      makeInventoryRow({
        routerModelId: 'bound-alias',
        catalogModelIds: ['other-model'],
        hasModelFile: true,
      }),
    ];

    const { rerender } = render(
      <LlamaCppAddForm
        {...makeAddProps({ onChange, inventory })}
      />,
    );

    const [installSourceSelect] = screen.getAllByRole('combobox');
    fireEvent.change(installSourceSelect, {
      target: { value: 'existingAlias' },
    });
    expect(onChange).toHaveBeenCalledWith({ llamaInstallSource: 'existingAlias' });

    rerender(
      <LlamaCppAddForm
        {...makeAddProps({
          onChange,
          inventory,
          value: {
            ...createEmptyAddModelWizardState('llama-cpp'),
            llamaInstallSource: 'existingAlias',
          },
        })}
      />,
    );

    expect(screen.getByRole('option', { name: 'orphan-alias' })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'bound-alias' })).not.toBeInTheDocument();
  });

  it('warns when chosen router alias already has catalog rows', () => {
    const inventory = [
      makeInventoryRow({
        routerModelId: 'taken-alias',
        catalogModelIds: ['existing-row'],
      }),
    ];

    render(
      <LlamaCppAddForm
        {...makeAddProps({
          inventory,
          value: {
            ...createEmptyAddModelWizardState('llama-cpp'),
            llamaRouterModelId: 'taken-alias',
          },
        })}
      />,
    );

    expect(screen.getByText(/Alias already has catalog rows/i)).toBeInTheDocument();
  });

  it('shows reasoning choices when selected profile exposes them', () => {
    render(
      <LlamaCppAddForm
        {...makeAddProps({
          value: {
            ...createEmptyAddModelWizardState('llama-cpp'),
            runtimeProfileId: 'qwen3_5',
          },
        })}
      />,
    );

    expect(screen.getByText(/Reasoning choices exposed by this profile/i)).toBeInTheDocument();
    expect(screen.getByText(/minimal, medium/)).toBeInTheDocument();
  });

  it('shows null reasoning message when profile has no choices', () => {
    render(
      <LlamaCppAddForm
        {...makeAddProps({
          profiles: [makeProfile({ thinkingControlJson: '{}' })],
          value: {
            ...createEmptyAddModelWizardState('llama-cpp'),
            runtimeProfileId: 'qwen3_5',
          },
        })}
      />,
    );

    expect(screen.getByText(/ReasoningChoicesJson will be null/i)).toBeInTheDocument();
  });

  it('inserts template runtime profile via quick action buttons', () => {
    const onChange = vi.fn();
    const onCreateRuntimeProfile = vi.fn(async () => {});

    render(
      <LlamaCppAddForm
        {...makeAddProps({ onChange, onCreateRuntimeProfile })}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Insert qwen3_5/i }));
    expect(onCreateRuntimeProfile).toHaveBeenCalledWith('qwen3_5');
    expect(onChange).toHaveBeenCalledWith({ runtimeProfileId: 'qwen3_5' });
  });

  it('toggles the inline custom runtime profile editor', () => {
    render(<LlamaCppAddForm {...makeAddProps()} />);

    fireEvent.click(screen.getByRole('button', { name: /Create custom/i }));
    expect(screen.getByRole('button', { name: /Create profile/i })).toBeInTheDocument();
    expect(screen.getByText('Profile ID')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Hide custom/i }));
    expect(screen.queryByRole('button', { name: /Create profile/i })).not.toBeInTheDocument();
  });

  it('surfaces custom profile validation errors before calling create', async () => {
    const onCreateCustomRuntimeProfile = vi.fn(async () => makeProfile({ profileId: 'custom_one' }));

    render(
      <LlamaCppAddForm
        {...makeAddProps({ onCreateCustomRuntimeProfile })}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Create custom/i }));
    fireEvent.click(screen.getByRole('button', { name: /Create profile/i }));

    await waitFor(() => {
      expect(screen.getByText(/Profile ID is required/i)).toBeInTheDocument();
    });
    expect(onCreateCustomRuntimeProfile).not.toHaveBeenCalled();
  });

  it('forwards repository picker changes into wizard state', () => {
    const onChange = vi.fn();

    render(<LlamaCppAddForm {...makeAddProps({ onChange })} />);

    fireEvent.change(screen.getByTestId('repo-input'), {
      target: { value: 'unsloth/Qwen3.5-9B-GGUF' },
    });
    expect(onChange).toHaveBeenCalledWith({
      llamaHuggingFaceRepository: 'unsloth/Qwen3.5-9B-GGUF',
    });

    fireEvent.click(screen.getByRole('button', { name: /Pick files/i }));
    expect(onChange).toHaveBeenCalledWith({
      llamaHuggingFaceQuantIncludePattern: 'Qwen3-9B-Q5_K_M.gguf',
      llamaHuggingFaceMmprojIncludePattern: 'mmproj-F16.gguf',
    });
  });

  it('updates optional router context and cache fields', () => {
    const onChange = vi.fn();

    render(<LlamaCppAddForm {...makeAddProps({ onChange })} />);

    const [contextInput] = screen.getAllByPlaceholderText('(container default)');
    fireEvent.change(contextInput, { target: { value: '8192' } });
    expect(onChange).toHaveBeenCalledWith({ llamaRouterContextSize: '8192' });
  });

  it('updates cache RAM and runtime profile selection', () => {
    const onChange = vi.fn();

    render(
      <LlamaCppAddForm
        {...makeAddProps({
          onChange,
          value: {
            ...createEmptyAddModelWizardState('llama-cpp'),
            runtimeProfileId: '',
          },
        })}
      />,
    );

    const cacheInput = screen.getAllByPlaceholderText('(container default)')[1];
    fireEvent.change(cacheInput, { target: { value: '1024' } });
    expect(onChange).toHaveBeenCalledWith({ llamaRouterCacheRamMib: '1024' });

    const profileSelect = screen.getAllByRole('combobox')[1];
    fireEvent.change(profileSelect, { target: { value: 'qwen3_5' } });
    expect(onChange).toHaveBeenCalledWith({ runtimeProfileId: 'qwen3_5' });
  });

  it('creates a custom runtime profile from the inline editor', async () => {
    const onChange = vi.fn();
    const onCreateCustomRuntimeProfile = vi.fn(async () =>
      makeProfile({ profileId: 'custom_one', displayName: 'Custom One' }),
    );

    render(
      <LlamaCppAddForm
        {...makeAddProps({ onChange, onCreateCustomRuntimeProfile })}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Create custom/i }));
    const customPanel = screen.getByRole('button', { name: /Create profile/i }).closest('.rounded.border');
    const [profileIdInput, displayNameInput] = within(customPanel as HTMLElement).getAllByRole('textbox');
    fireEvent.change(profileIdInput, { target: { value: 'custom_one' } });
    fireEvent.change(displayNameInput, { target: { value: 'Custom One' } });
    fireEvent.click(screen.getByRole('button', { name: /Create profile/i }));

    await waitFor(() => {
      expect(onCreateCustomRuntimeProfile).toHaveBeenCalled();
      expect(onChange).toHaveBeenCalledWith({ runtimeProfileId: 'custom_one' });
    });
  });

  it('updates attach-existing alias and hugging face router fields', () => {
    const onChange = vi.fn();
    const inventory = [
      makeInventoryRow({
        routerModelId: 'orphan-alias',
        catalogModelIds: [],
        hasModelFile: true,
      }),
    ];

    const { rerender } = render(
      <LlamaCppAddForm
        {...makeAddProps({
          onChange,
          inventory,
          value: {
            ...createEmptyAddModelWizardState('llama-cpp'),
            llamaInstallSource: 'existingAlias',
          },
        })}
      />,
    );

    const aliasSelect = screen.getByText('Existing Alias').parentElement?.querySelector('select');
    fireEvent.change(aliasSelect!, { target: { value: 'orphan-alias' } });
    expect(onChange).toHaveBeenCalledWith({ llamaExistingAliasRouterModelId: 'orphan-alias' });

    rerender(
      <LlamaCppAddForm
        {...makeAddProps({
          onChange,
          value: {
            ...createEmptyAddModelWizardState('llama-cpp'),
            llamaRouterModelId: 'QwenAlias',
            llamaHuggingFaceTargetDirectory: 'QwenAlias',
          },
        })}
      />,
    );

    fireEvent.change(screen.getAllByDisplayValue('QwenAlias')[0], { target: { value: 'RenamedAlias' } });
    expect(onChange).toHaveBeenCalledWith({ llamaRouterModelId: 'RenamedAlias' });

    const targetDirectoryInput = screen.getByText('Target Directory').parentElement?.querySelector('input');
    fireEvent.change(targetDirectoryInput!, { target: { value: 'models/Qwen' } });
    expect(onChange).toHaveBeenCalledWith({ llamaHuggingFaceTargetDirectory: 'models/Qwen' });
  });
});

describe('LlamaCppEditForm', () => {
  it('renders read-only router alias and live inventory details', () => {
    render(
      <LlamaCppEditForm
        value={makeEditState()}
        onChange={vi.fn()}
        profiles={[makeProfile()]}
        inventory={[makeInventoryRow()]}
      />,
    );

    expect(screen.getByDisplayValue('QwenAlias')).toBeDisabled();
    expect(screen.getByText('loaded')).toBeInTheDocument();
    expect(screen.getByText(/model\.gguf/)).toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();
  });

  it('warns when live inventory has no matching alias', () => {
    render(
      <LlamaCppEditForm
        value={makeEditState({ localRuntimeRouterModelId: 'missing-alias' })}
        onChange={vi.fn()}
        profiles={[makeProfile()]}
        inventory={[]}
      />,
    );

    expect(screen.getByText(/no longer exists/i)).toBeInTheDocument();
  });

  it('shows other catalog rows sharing the same alias', () => {
    render(
      <LlamaCppEditForm
        value={makeEditState({ modelId: 'qwen-local' })}
        onChange={vi.fn()}
        profiles={[makeProfile()]}
        inventory={[
          makeInventoryRow({
            catalogModelIds: ['qwen-local', 'shared-model'],
          }),
        ]}
      />,
    );

    expect(screen.getByText('shared-model')).toBeInTheDocument();
  });

  it('toggles advanced load params and parallel tool calls', () => {
    const onChange = vi.fn();

    render(
      <LlamaCppEditForm
        value={makeEditState()}
        onChange={onChange}
        profiles={[makeProfile()]}
        inventory={[makeInventoryRow()]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Show advanced/i }));
    fireEvent.change(screen.getByPlaceholderText(/"model": "QwenAlias"/i), {
      target: { value: '{"model":"QwenAlias"}' },
    });
    expect(onChange).toHaveBeenCalledWith({
      localRuntimeLoadParamsJson: '{"model":"QwenAlias"}',
    });

    fireEvent.click(screen.getByRole('checkbox', { name: /Allow parallel tool calls/i }));
    expect(onChange).toHaveBeenCalledWith({ localRuntimeParallelToolCalls: true });
  });

  it('updates router context overrides in edit mode', () => {
    const onChange = vi.fn();

    render(
      <LlamaCppEditForm
        value={makeEditState()}
        onChange={onChange}
        profiles={[makeProfile()]}
        inventory={[makeInventoryRow()]}
      />,
    );

    fireEvent.change(screen.getAllByPlaceholderText('(container default)')[0], {
      target: { value: '16384' },
    });
    expect(onChange).toHaveBeenCalledWith({ localRuntimeRouterContextSize: '16384' });
  });

  it('handles invalid thinking control JSON without reasoning choices', () => {
    render(
      <LlamaCppEditForm
        value={makeEditState({ runtimeProfileId: 'broken-profile' })}
        onChange={vi.fn()}
        profiles={[makeProfile({ profileId: 'broken-profile', thinkingControlJson: 'not-json' })]}
        inventory={[makeInventoryRow()]}
      />,
    );

    expect(screen.queryByText(/Reasoning choices exposed/i)).not.toBeInTheDocument();
  });

  it('updates prompt cache RAM override in edit mode', () => {
    const onChange = vi.fn();

    render(
      <LlamaCppEditForm
        value={makeEditState()}
        onChange={onChange}
        profiles={[makeProfile()]}
        inventory={[makeInventoryRow()]}
      />,
    );

    fireEvent.change(screen.getAllByPlaceholderText('(container default)')[1], {
      target: { value: '2048' },
    });
    expect(onChange).toHaveBeenCalledWith({ localRuntimeRouterCacheRamMib: '2048' });
  });
});
