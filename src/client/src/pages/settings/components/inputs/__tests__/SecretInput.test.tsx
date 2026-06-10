import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SecretInput } from '../SecretInput';

describe('SecretInput', () => {
  it('shows stored hint when a value exists server-side and input is empty', () => {
    render(<SecretInput value="" onChange={() => {}} storedHasValue />);
    expect(screen.getByText(/credential is already saved/i)).toBeInTheDocument();
  });

  it('does not show stored hint when user has entered text', () => {
    render(<SecretInput value="x" onChange={() => {}} storedHasValue />);
    expect(screen.queryByText(/credential is already saved/i)).not.toBeInTheDocument();
  });

  it('calls onChange when the user types', async () => {
    const onChange = vi.fn();
    render(<SecretInput value="" onChange={onChange} placeholder="Enter key" />);
    const input = screen.getByPlaceholderText('Enter key');
    await userEvent.type(input, 'abc');
    expect(onChange).toHaveBeenCalled();
  });
});
