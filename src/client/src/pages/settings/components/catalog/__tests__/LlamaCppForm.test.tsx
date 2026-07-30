import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { createEmptyAddModelWizardState } from '../../../utils';
import { LlamaCppAddForm, LlamaCppEditForm } from '../providers/LlamaCppForm';
import type { ProviderAddForm } from '../providers/types';
import type { CatalogEditState } from '../../../types';
vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      getLlamaInstallationDetail: vi.fn(),
      getLlamaRouterEntries: vi.fn(),
    },
  },
}));

vi.mock('../../../../../features/localModelOnboarding/advanced/ArtifactGroupPicker', () => ({
  ArtifactGroupPicker: () => <div data-testid="artifact-group-picker" />,
}));

vi.mock('../../../../../features/localModelOnboarding/installed/LlamaInstalledSummary', () => ({
  LlamaInstalledSummary: ({ modelId }: { modelId: string }) => (
    <div data-testid="installed-summary">{modelId}</div>
  ),
}));

function makeAddProps(overrides: Partial<ProviderAddForm> = {}): ProviderAddForm {
  return {
    value: {
      ...createEmptyAddModelWizardState('llama-cpp'),
      llamaRouterModelId: 'Custom-Alias',
      catalogModelId: 'custom-local',
    },
    onChange: vi.fn(),
    inventory: [],
    ...overrides,
  };
}

function makeEditState(): CatalogEditState {
  return {
    modelId: 'qwen-local',
    provider: 'llama-cpp',
    displayName: 'Qwen Local',
    description: '',
    displayOrder: '',
    isActive: true,
    samplingParametersJson: '{}',
    reasoningChoicesJson: '',
    thinkingControlJson: '{}',
    requestFieldsWhenToolsPresentJson: '{}',
    combineSystemAndDeveloperMessages: true,
    thoughtBlockPattern: '',
  };
}

describe('LlamaCppAddForm', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders custom explicit HF form by default', () => {
    render(<LlamaCppAddForm {...makeAddProps()} />);
    expect(screen.getByTestId('artifact-group-picker')).toBeInTheDocument();
    expect(screen.getByText(/Custom Hugging Face install requires explicit revision/i)).toBeInTheDocument();
  });

  it('renders attach form for existing alias source', () => {
    render(
      <LlamaCppAddForm
        {...makeAddProps({
          value: {
            ...createEmptyAddModelWizardState('llama-cpp'),
            llamaInstallSource: 'existingAlias',
          },
        })}
      />
    );
    expect(screen.getByText(/Attach binds a catalog identity/i)).toBeInTheDocument();
  });
});

describe('LlamaCppEditForm', () => {
  it('renders installed summary instead of legacy advanced fields', () => {
    render(<LlamaCppEditForm value={makeEditState()} onChange={vi.fn()} />);
    expect(screen.getByTestId('installed-summary')).toHaveTextContent('qwen-local');
    expect(screen.queryByText(/load params json/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/parallel tool calls/i)).not.toBeInTheDocument();
  });
});
