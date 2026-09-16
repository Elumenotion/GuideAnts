import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import {
  NonLocalModelParameterSurfaceEditor,
  type NonLocalModelParameterSurfaceValue,
} from '../NonLocalModelParameterSurfaceEditor';

function baseValue(): NonLocalModelParameterSurfaceValue {
  return {
    samplingParametersJson: '{}',
    reasoningChoicesJson: '',
    thinkingControlJson: '{}',
    requestFieldsWhenToolsPresentJson: '{}',
  };
}

// Mirrors how a real caller (CatalogRowEditModal, AddModelWizard) owns and feeds back the value -
// the editor itself is a plain controlled component with no state of its own.
function Harness() {
  const [value, setValue] = useState(baseValue());
  return (
    <NonLocalModelParameterSurfaceEditor
      provider="openai-responses"
      value={value}
      onChange={(updates) => setValue((previous) => ({ ...previous, ...updates }))}
    />
  );
}

// userEvent's keyboard DSL treats "[" as the start of a key descriptor, so a literal "[" must be
// typed as "[[".
const escapeForUserEventType = (text: string) => text.replace(/\[/g, '[[');

describe('NonLocalModelParameterSurfaceEditor - Reasoning Choices JSON', () => {
  it('binds directly to the stored JSON, so every character (commas included) is kept as typed', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const typed = '["low",';

    const input = screen.getByPlaceholderText('["none", "low", "medium", "high"]');
    await user.type(input, escapeForUserEventType(typed));

    // Unlike a derived/round-tripped display, a directly-bound JSON field never snaps back
    // mid-edit - the box shows exactly what was typed, including an interior comma.
    expect(input).toHaveValue(typed);
  });

  it('accepts a full JSON array typed into the box', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const typed = '["low","medium","high"]';

    const input = screen.getByPlaceholderText('["none", "low", "medium", "high"]');
    await user.type(input, escapeForUserEventType(typed));

    expect(input).toHaveValue(typed);
    expect(screen.queryByText(/Reasoning choices .*must be/i)).not.toBeInTheDocument();
  });

  it('shows a validation error for invalid JSON without altering the box contents', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    const input = screen.getByPlaceholderText('["none", "low", "medium", "high"]');
    await user.type(input, 'not json');

    expect(input).toHaveValue('not json');
    expect(screen.getByText('Reasoning choices JSON must be valid JSON.')).toBeInTheDocument();
  });
});
