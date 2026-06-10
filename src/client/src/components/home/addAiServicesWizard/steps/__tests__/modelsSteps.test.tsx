import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { HuggingFaceModelsStep } from '../HuggingFaceModelsStep';
import { ModelsStep } from '../ModelsStep';
import { OpenAiModelsStep } from '../OpenAiModelsStep';
import { OpenRouterModelsStep } from '../OpenRouterModelsStep';

describe('ModelsStep', () => {
  const onDraftModelIdChange = vi.fn();
  const onDraftProviderChange = vi.fn();
  const onSetDraftAsGlobalDefaultChange = vi.fn();
  const onAddModel = vi.fn();
  const onRemoveDraftModel = vi.fn();

  const defaultProps = {
    existingModels: [],
    draftModels: [],
    draftModelId: '',
    draftProvider: 'Completions' as const,
    setDraftAsGlobalDefault: false,
    lockDraftAsGlobalDefault: false,
    addError: null,
    validationError: null,
    onDraftModelIdChange,
    onDraftProviderChange,
    onSetDraftAsGlobalDefaultChange,
    onAddModel,
    onRemoveDraftModel,
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders empty state and draft form controls', () => {
    render(<ModelsStep {...defaultProps} />);

    expect(screen.getByText('Models (required)')).toBeInTheDocument();
    expect(screen.getByText(/Add at least one model to continue/)).toBeInTheDocument();
    expect(screen.getByLabelText('Model')).toBeInTheDocument();
    expect(screen.getByLabelText('Provider')).toBeInTheDocument();
  });

  it('fires draft input handlers and add model action', () => {
    render(<ModelsStep {...defaultProps} />);

    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'gpt-4o' } });
    fireEvent.change(screen.getByLabelText('Provider'), { target: { value: 'Responses' } });
    fireEvent.click(screen.getByLabelText(/Set this model as the global default chat model/));
    fireEvent.click(screen.getByRole('button', { name: 'Add model' }));

    expect(onDraftModelIdChange).toHaveBeenCalledWith('gpt-4o');
    expect(onDraftProviderChange).toHaveBeenCalledWith('Responses');
    expect(onSetDraftAsGlobalDefaultChange).toHaveBeenCalledWith(true);
    expect(onAddModel).toHaveBeenCalled();
  });

  it('lists existing and draft models with errors and locked default hint', () => {
    render(
      <ModelsStep
        {...defaultProps}
        existingModels={[{ modelId: 'gpt-4o', provider: 'Completions' }]}
        draftModels={[
          {
            localId: 'draft-1',
            modelId: 'gpt-4.1',
            provider: 'Responses',
            setAsGlobalDefault: true,
          },
        ]}
        lockDraftAsGlobalDefault
        addError="Duplicate model"
        validationError="Add a model"
      />,
    );

    expect(screen.getByText('gpt-4o')).toBeInTheDocument();
    expect(screen.getByText('Already configured')).toBeInTheDocument();
    expect(screen.getByText('gpt-4.1')).toBeInTheDocument();
    expect(screen.getByText('Global default')).toBeInTheDocument();
    expect(screen.getByText(/first configured model is always set/)).toBeInTheDocument();
    expect(screen.getByText('Duplicate model')).toBeInTheDocument();
    expect(screen.getByText('Add a model')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    expect(onRemoveDraftModel).toHaveBeenCalledWith('draft-1');
  });
});

describe('OpenAiModelsStep', () => {
  const onDraftModelIdChange = vi.fn();
  const onDraftProviderChange = vi.fn();
  const onSetDraftAsGlobalDefaultChange = vi.fn();
  const onAddModel = vi.fn();
  const onRemoveDraftModel = vi.fn();

  const defaultProps = {
    existingModels: [],
    draftModels: [],
    draftModelId: '',
    draftProvider: 'Completions' as const,
    setDraftAsGlobalDefault: false,
    lockDraftAsGlobalDefault: false,
    addError: null,
    validationError: null,
    onDraftModelIdChange,
    onDraftProviderChange,
    onSetDraftAsGlobalDefaultChange,
    onAddModel,
    onRemoveDraftModel,
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders empty state and draft form controls', () => {
    render(<OpenAiModelsStep {...defaultProps} />);

    expect(screen.getByText('OpenAI models (required)')).toBeInTheDocument();
    expect(screen.getByText(/Add at least one OpenAI model to continue/)).toBeInTheDocument();
    expect(screen.getByLabelText('Model')).toBeInTheDocument();
    expect(screen.getByLabelText('Provider')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add model' })).toBeInTheDocument();
  });

  it('fires change handlers for draft inputs and add model', () => {
    render(<OpenAiModelsStep {...defaultProps} />);

    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'gpt-4.1-nano' } });
    fireEvent.change(screen.getByLabelText('Provider'), { target: { value: 'Responses' } });
    fireEvent.click(screen.getByLabelText(/Set this model as the global default chat model/));
    fireEvent.click(screen.getByRole('button', { name: 'Add model' }));

    expect(onDraftModelIdChange).toHaveBeenCalledWith('gpt-4.1-nano');
    expect(onDraftProviderChange).toHaveBeenCalledWith('Responses');
    expect(onSetDraftAsGlobalDefaultChange).toHaveBeenCalledWith(true);
    expect(onAddModel).toHaveBeenCalled();
  });

  it('lists existing and draft models and supports remove', () => {
    render(
      <OpenAiModelsStep
        {...defaultProps}
        existingModels={[{ modelId: 'gpt-4o', provider: 'Completions' }]}
        draftModels={[
          {
            localId: 'draft-1',
            modelId: 'gpt-4.1',
            provider: 'Responses',
            setAsGlobalDefault: true,
          },
        ]}
        lockDraftAsGlobalDefault
        addError="Duplicate model"
        validationError="Add a model"
      />,
    );

    expect(screen.getByText('gpt-4o')).toBeInTheDocument();
    expect(screen.getByText('Already configured')).toBeInTheDocument();
    expect(screen.getByText('gpt-4.1')).toBeInTheDocument();
    expect(screen.getByText('Global default')).toBeInTheDocument();
    expect(screen.getByText(/first configured model is always set/)).toBeInTheDocument();
    expect(screen.getByText('Duplicate model')).toBeInTheDocument();
    expect(screen.getByText('Add a model')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    expect(onRemoveDraftModel).toHaveBeenCalledWith('draft-1');
  });
});

describe('HuggingFaceModelsStep', () => {
  const onDraftModelIdChange = vi.fn();
  const onSetDraftAsGlobalDefaultChange = vi.fn();
  const onAddModel = vi.fn();
  const onRemoveDraftModel = vi.fn();

  const defaultProps = {
    existingModels: [],
    draftModels: [],
    draftModelId: '',
    setDraftAsGlobalDefault: false,
    lockDraftAsGlobalDefault: false,
    addError: null,
    validationError: null,
    onDraftModelIdChange,
    onSetDraftAsGlobalDefaultChange,
    onAddModel,
    onRemoveDraftModel,
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders empty state and model form', () => {
    render(<HuggingFaceModelsStep {...defaultProps} />);

    expect(screen.getByText('Hugging Face chat models (required)')).toBeInTheDocument();
    expect(screen.getByText(/Add at least one Hugging Face model to continue/)).toBeInTheDocument();
    expect(screen.getByLabelText('Model')).toBeInTheDocument();
  });

  it('fires change handlers for draft inputs and add model', () => {
    render(<HuggingFaceModelsStep {...defaultProps} />);

    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'meta-llama/Llama-3.1-8B' } });
    fireEvent.click(screen.getByLabelText(/Set this model as the global default chat model/));
    fireEvent.click(screen.getByRole('button', { name: 'Add model' }));

    expect(onDraftModelIdChange).toHaveBeenCalledWith('meta-llama/Llama-3.1-8B');
    expect(onSetDraftAsGlobalDefaultChange).toHaveBeenCalledWith(true);
    expect(onAddModel).toHaveBeenCalled();
  });

  it('lists existing and draft models with hf-inference-chat badge', () => {
    render(
      <HuggingFaceModelsStep
        {...defaultProps}
        existingModels={[{ modelId: 'Qwen/Qwen3-9B' }]}
        draftModels={[
          {
            localId: 'hf-draft',
            modelId: 'microsoft/phi-4',
            setAsGlobalDefault: false,
          },
        ]}
        validationError="Need a model"
      />,
    );

    expect(screen.getAllByText('hf-inference-chat').length).toBeGreaterThanOrEqual(2);
    expect(screen.getByText('microsoft/phi-4')).toBeInTheDocument();
    expect(screen.getByText('Need a model')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    expect(onRemoveDraftModel).toHaveBeenCalledWith('hf-draft');
  });
});

describe('OpenRouterModelsStep', () => {
  const onDraftModelIdChange = vi.fn();
  const onSetDraftAsGlobalDefaultChange = vi.fn();
  const onAddModel = vi.fn();
  const onRemoveDraftModel = vi.fn();

  const defaultProps = {
    existingModels: [],
    draftModels: [],
    draftModelId: '',
    setDraftAsGlobalDefault: false,
    lockDraftAsGlobalDefault: false,
    addError: null,
    validationError: null,
    onDraftModelIdChange,
    onSetDraftAsGlobalDefaultChange,
    onAddModel,
    onRemoveDraftModel,
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders empty state and model form', () => {
    render(<OpenRouterModelsStep {...defaultProps} />);

    expect(screen.getByText('OpenRouter models (required)')).toBeInTheDocument();
    expect(screen.getByText(/Add at least one OpenRouter model to continue/)).toBeInTheDocument();
    expect(screen.getByLabelText('Model')).toBeInTheDocument();
  });

  it('fires change handlers for draft inputs and add model', () => {
    render(<OpenRouterModelsStep {...defaultProps} />);

    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'minimax/minimax-m3' } });
    fireEvent.click(screen.getByLabelText(/Set this model as the global default chat model/));
    fireEvent.click(screen.getByRole('button', { name: 'Add model' }));

    expect(onDraftModelIdChange).toHaveBeenCalledWith('minimax/minimax-m3');
    expect(onSetDraftAsGlobalDefaultChange).toHaveBeenCalledWith(true);
    expect(onAddModel).toHaveBeenCalled();
  });

  it('lists existing and draft models and supports remove', () => {
    render(
      <OpenRouterModelsStep
        {...defaultProps}
        existingModels={[{ modelId: 'openai/gpt-4o' }]}
        draftModels={[
          {
            localId: 'or-draft',
            modelId: 'anthropic/claude-3.5-sonnet',
            setAsGlobalDefault: true,
          },
        ]}
        addError="Already added"
      />,
    );

    expect(screen.getByText('openai/gpt-4o')).toBeInTheDocument();
    expect(screen.getByText('anthropic/claude-3.5-sonnet')).toBeInTheDocument();
    expect(screen.getByText('Global default')).toBeInTheDocument();
    expect(screen.getByText('Already added')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    expect(onRemoveDraftModel).toHaveBeenCalledWith('or-draft');
  });
});
