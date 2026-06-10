import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { EnumSelect } from '../EnumSelect';

describe('EnumSelect', () => {
  it('renders all options and reflects the current value', () => {
    render(
      <EnumSelect
        value="beta"
        options={['alpha', 'beta', 'gamma']}
        onChange={() => {}}
      />,
    );

    const select = screen.getByRole('combobox') as HTMLSelectElement;
    expect(select.value).toBe('beta');
    expect(screen.getByRole('option', { name: 'alpha' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'gamma' })).toBeInTheDocument();
  });

  it('calls onChange when a new option is selected', () => {
    const onChange = vi.fn();
    render(
      <EnumSelect value="alpha" options={['alpha', 'beta']} onChange={onChange} />,
    );

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'beta' } });
    expect(onChange).toHaveBeenCalledWith('beta');
  });
});
