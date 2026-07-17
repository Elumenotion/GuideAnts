import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { KeyValueEditor } from '../KeyValueEditor';

describe('KeyValueEditor', () => {
  it('trims leading and trailing whitespace from keys and values on blur', () => {
    const onChange = vi.fn();
    render(
      <KeyValueEditor
        items={[{ key: 'files', value: '\t[@files]\t' }]}
        onChange={onChange}
      />
    );

    const [keyInput, valueInput] = screen.getAllByRole('textbox');

    fireEvent.blur(keyInput);
    fireEvent.blur(valueInput);

    expect(onChange).toHaveBeenCalledWith([{ key: 'files', value: '[@files]' }]);
  });

  it('does not call onChange on blur when values are already trimmed', () => {
    const onChange = vi.fn();
    render(
      <KeyValueEditor
        items={[{ key: 'files', value: '[@files]' }]}
        onChange={onChange}
      />
    );

    const [keyInput, valueInput] = screen.getAllByRole('textbox');

    fireEvent.blur(keyInput);
    fireEvent.blur(valueInput);

    expect(onChange).not.toHaveBeenCalled();
  });
});
