import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import DropdownSearch from '../DropdownSearch';

describe('DropdownSearch', () => {
  it('renders with default placeholder and value', () => {
    render(<DropdownSearch value="" onChange={vi.fn()} />);
    expect(screen.getByPlaceholderText('Search...')).toBeInTheDocument();
  });

  it('uses custom placeholder and className', () => {
    const { container } = render(
      <DropdownSearch value="q" onChange={vi.fn()} placeholder="Filter" className="extra" />
    );
    expect(screen.getByPlaceholderText('Filter')).toHaveValue('q');
    expect(container.firstChild).toHaveClass('extra');
  });

  it('calls onChange when typing', () => {
    const onChange = vi.fn();
    render(<DropdownSearch value="" onChange={onChange} />);
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'abc' } });
    expect(onChange).toHaveBeenCalledWith('abc');
  });
});
