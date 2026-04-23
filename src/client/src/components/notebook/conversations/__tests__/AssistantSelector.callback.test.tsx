import React from 'react';
import { render, fireEvent } from '../../../../test/test-utils';
import AssistantSelector, { AssistantOption } from '../AssistantSelector';
import { describe, it, expect, vi } from 'vitest';

const assistants: AssistantOption[] = [
  { name: 'GPT-4', model: 'gpt-4o', avatarUrl: '' },
  { name: 'Claude', model: 'claude-3', avatarUrl: '' },
];

describe('AssistantSelector', () => {
  it('calls onSelect with chosen assistant name', () => {
    const onSelect = vi.fn();
    const { getByRole, getByText } = render(
      <AssistantSelector
        assistants={assistants}
        selectedName="GPT-4"
        onSelect={onSelect}
      />
    );

    // Click trigger button to open dropdown
    const trigger = getByRole('button');
    fireEvent.click(trigger);

    // Click second assistant option
    const option = getByText('Claude');
    fireEvent.click(option);

    expect(onSelect).toHaveBeenCalledWith('Claude');
  });
}); 