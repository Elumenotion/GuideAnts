import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ModelIdTypeahead } from '../ModelIdTypeahead';

describe('ModelIdTypeahead', () => {
  it('shows no dropdown when provider has no known models', () => {
    render(
      <ModelIdTypeahead
        provider="llama-cpp"
        value=""
        onChange={vi.fn()}
        onSelectSuggestion={vi.fn()}
      />
    );
    fireEvent.focus(screen.getByRole('textbox'));
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(screen.queryByRole('list')).not.toBeInTheDocument();
  });

  it('shows all provider models on empty input focus', () => {
    render(
      <ModelIdTypeahead
        provider="azure-openai-chat"
        value=""
        onChange={vi.fn()}
        onSelectSuggestion={vi.fn()}
      />
    );
    fireEvent.focus(screen.getByRole('textbox'));
    expect(screen.getByText('gpt-4.1')).toBeInTheDocument();
    expect(screen.getByText('gpt-4o')).toBeInTheDocument();
  });

  it('filters suggestions by typed model id substring', () => {
    render(
      <ModelIdTypeahead
        provider="azure-openai-chat"
        value="mini"
        onChange={vi.fn()}
        onSelectSuggestion={vi.fn()}
      />
    );
    fireEvent.focus(screen.getByRole('textbox'));
    expect(screen.getByText('gpt-4.1-mini')).toBeInTheDocument();
    expect(screen.getByText('gpt-4o-mini')).toBeInTheDocument();
    expect(screen.queryByText('gpt-4.1')).not.toBeInTheDocument();
  });

  it('filters suggestions by display name substring', () => {
    render(
      <ModelIdTypeahead
        provider="anthropic"
        value="Sonnet"
        onChange={vi.fn()}
        onSelectSuggestion={vi.fn()}
      />
    );
    fireEvent.focus(screen.getByRole('textbox'));
    expect(screen.getByText('claude-sonnet-4-5')).toBeInTheDocument();
    expect(screen.queryByText('claude-haiku-4-5')).not.toBeInTheDocument();
  });

  it('only shows models for the given provider', () => {
    render(
      <ModelIdTypeahead
        provider="openai-responses"
        value=""
        onChange={vi.fn()}
        onSelectSuggestion={vi.fn()}
      />
    );
    fireEvent.focus(screen.getByRole('textbox'));
    expect(screen.getAllByText('o3')[0]).toBeInTheDocument();
    // openai-chat models must not appear
    expect(screen.queryByText('gpt-4o')).not.toBeInTheDocument();
  });

  it('calls onSelectSuggestion with full model data on click', () => {
    const onSelect = vi.fn();
    render(
      <ModelIdTypeahead
        provider="azure-openai-chat"
        value=""
        onChange={vi.fn()}
        onSelectSuggestion={onSelect}
      />
    );
    fireEvent.focus(screen.getByRole('textbox'));
    fireEvent.mouseDown(screen.getByText('gpt-4.1'));
    expect(onSelect).toHaveBeenCalledWith(
      expect.objectContaining({
        modelId: 'gpt-4.1',
        displayName: 'GPT-4.1',
        runtimeProfileId: 'openai_chat_standard',
        reasoningEffortEnabled: false,
      })
    );
  });

  it('calls onSelectSuggestion with reasoning model data', () => {
    const onSelect = vi.fn();
    render(
      <ModelIdTypeahead
        provider="azure-openai-responses"
        value=""
        onChange={vi.fn()}
        onSelectSuggestion={onSelect}
      />
    );
    fireEvent.focus(screen.getByRole('textbox'));
    fireEvent.mouseDown(screen.getAllByText('o3')[0]);
    expect(onSelect).toHaveBeenCalledWith(
      expect.objectContaining({
        modelId: 'o3',
        runtimeProfileId: 'openai_responses_reasoning',
        reasoningEffortEnabled: true,
      })
    );
  });

  it('closes dropdown on Escape', () => {
    render(
      <ModelIdTypeahead
        provider="anthropic"
        value=""
        onChange={vi.fn()}
        onSelectSuggestion={vi.fn()}
      />
    );
    const input = screen.getByRole('textbox');
    fireEvent.focus(input);
    expect(screen.getByText('claude-opus-4-5')).toBeInTheDocument();
    fireEvent.keyDown(input, { key: 'Escape' });
    expect(screen.queryByText('claude-opus-4-5')).not.toBeInTheDocument();
  });

  it('keyboard navigation selects with Enter', () => {
    const onSelect = vi.fn();
    render(
      <ModelIdTypeahead
        provider="anthropic"
        value=""
        onChange={vi.fn()}
        onSelectSuggestion={onSelect}
      />
    );
    const input = screen.getByRole('textbox');
    fireEvent.focus(input);
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onSelect).toHaveBeenCalledTimes(1);
  });

  it('calls onChange on free-text input without triggering onSelectSuggestion', () => {
    const onChange = vi.fn();
    const onSelect = vi.fn();
    render(
      <ModelIdTypeahead
        provider="azure-openai-chat"
        value=""
        onChange={onChange}
        onSelectSuggestion={onSelect}
      />
    );
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'my-custom-model' } });
    expect(onChange).toHaveBeenCalledWith('my-custom-model');
    expect(onSelect).not.toHaveBeenCalled();
  });
});
