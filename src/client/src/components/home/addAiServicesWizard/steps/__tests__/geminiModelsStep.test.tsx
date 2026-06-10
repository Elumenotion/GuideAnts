import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { GeminiModelsStep } from '../GeminiModelsStep';

const baseProps = {
  existingModels: [{ modelId: 'gemini-1', displayName: 'Gemini 1', isActive: true }],
  draftModels: [{ localId: 'd1', modelId: 'gemini-2.5-flash', displayName: 'Flash' }],
  draftModelId: '',
  setDraftAsGlobalDefault: false,
  lockDraftAsGlobalDefault: false,
  addError: null as string | null,
  validationError: null as string | null,
  onDraftModelIdChange: vi.fn(),
  onSetDraftAsGlobalDefaultChange: vi.fn(),
  onAddModel: vi.fn(),
  onRemoveDraftModel: vi.fn(),
};

describe('GeminiModelsStep', () => {
  it('renders existing and draft models', () => {
    render(<GeminiModelsStep {...baseProps} />);
    expect(screen.getByText(/gemini models \(required\)/i)).toBeInTheDocument();
    expect(screen.getByText('gemini-1')).toBeInTheDocument();
    expect(screen.getByText('gemini-2.5-flash')).toBeInTheDocument();
  });

  it('updates draft model id on input', () => {
    const onDraftModelIdChange = vi.fn();
    render(<GeminiModelsStep {...baseProps} onDraftModelIdChange={onDraftModelIdChange} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Model' }), { target: { value: 'gemini-pro' } });
    expect(onDraftModelIdChange).toHaveBeenCalledWith('gemini-pro');
  });

  it('calls onAddModel from add button', () => {
    const onAddModel = vi.fn();
    render(<GeminiModelsStep {...baseProps} onAddModel={onAddModel} />);
    fireEvent.click(screen.getByRole('button', { name: /add model/i }));
    expect(onAddModel).toHaveBeenCalled();
  });

  it('shows validation and add errors', () => {
    render(
      <GeminiModelsStep
        {...baseProps}
        validationError="Model id required"
        addError="Duplicate model"
      />
    );
    expect(screen.getByText('Model id required')).toBeInTheDocument();
    expect(screen.getByText('Duplicate model')).toBeInTheDocument();
  });

  it('toggles global default checkbox when not locked', () => {
    const onSetDraftAsGlobalDefaultChange = vi.fn();
    render(
      <GeminiModelsStep
        {...baseProps}
        onSetDraftAsGlobalDefaultChange={onSetDraftAsGlobalDefaultChange}
      />
    );
    fireEvent.click(screen.getByRole('checkbox', { name: /global default chat model/i }));
    expect(onSetDraftAsGlobalDefaultChange).toHaveBeenCalledWith(true);
  });

  it('removes draft model', () => {
    const onRemoveDraftModel = vi.fn();
    render(<GeminiModelsStep {...baseProps} onRemoveDraftModel={onRemoveDraftModel} />);
    fireEvent.click(screen.getByRole('button', { name: /remove/i }));
    expect(onRemoveDraftModel).toHaveBeenCalledWith('d1');
  });
});
