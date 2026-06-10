import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { UrlInput } from '../UrlInput';

describe('UrlInput', () => {
  it('renders url input with placeholder', () => {
    render(
      <UrlInput
        value="https://example.com"
        onChange={() => {}}
        placeholder="https://api.example.com"
      />,
    );

    const input = screen.getByPlaceholderText('https://api.example.com') as HTMLInputElement;
    expect(input.type).toBe('url');
    expect(input.value).toBe('https://example.com');
  });

  it('calls onChange with the edited value', () => {
    const onChange = vi.fn();
    render(<UrlInput value="" onChange={onChange} />);

    fireEvent.change(screen.getByRole('textbox'), {
      target: { value: 'https://localhost:8080' },
    });
    expect(onChange).toHaveBeenCalledWith('https://localhost:8080');
  });
});
