import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { IntInput } from '../IntInput';

describe('IntInput', () => {
  it('renders numeric value with min and max attributes', () => {
    render(<IntInput value={42} onChange={() => {}} min={0} max={100} />);

    const input = screen.getByRole('spinbutton') as HTMLInputElement;
    expect(input.value).toBe('42');
    expect(input.min).toBe('0');
    expect(input.max).toBe('100');
  });

  it('emits empty string when input is cleared', () => {
    const onChange = vi.fn();
    render(<IntInput value={10} onChange={onChange} />);

    fireEvent.change(screen.getByRole('spinbutton'), { target: { value: '' } });
    expect(onChange).toHaveBeenCalledWith('');
  });

  it('parses integer values from user input', () => {
    const onChange = vi.fn();
    render(<IntInput value="" onChange={onChange} />);

    fireEvent.change(screen.getByRole('spinbutton'), { target: { value: '15' } });
    expect(onChange).toHaveBeenCalledWith(15);
  });
});
