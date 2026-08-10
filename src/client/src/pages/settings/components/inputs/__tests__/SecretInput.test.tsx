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

  it('uses autoComplete="new-password" so browser password managers do not autofill it', () => {
    // autoComplete="off" is ignored by password managers on type="password" inputs; this is
    // the attribute that actually suppresses the Foundry Connections autofill-overwrite bug.
    render(<SecretInput value="" onChange={() => {}} />);
    const input = document.querySelector('input[type="password"]') as HTMLInputElement;
    expect(input.autocomplete).toBe('new-password');
  });

  it('forwards id, disabled, and tabIndex for use in a labelled form grid', () => {
    render(<SecretInput value="" onChange={() => {}} id="Section-ApiKey" disabled tabIndex={3} />);
    const input = document.getElementById('Section-ApiKey') as HTMLInputElement;
    expect(input).toBeInTheDocument();
    expect(input.disabled).toBe(true);
    expect(input.tabIndex).toBe(3);
  });
});
