import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { AddLinkForm } from '../AddLinkForm';

// Prevent window.open / electron side-effects
vi.stubGlobal('open', vi.fn());

const typeUrl = async (input: HTMLInputElement, value: string) => {
  await userEvent.clear(input);
  await userEvent.type(input, value);
};

describe('AddLinkForm', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('shows validation error when submitting empty or invalid URL', async () => {
    const onAdd = vi.fn();
    render(<AddLinkForm onAdd={onAdd} onCancel={vi.fn()} />);

    // Enter invalid URL and attempt submit
    const input = screen.getByLabelText(/url/i);
    await typeUrl(input as HTMLInputElement, 'invalid-url');
    await userEvent.click(screen.getByRole('button', { name: /add link/i }));

    expect(onAdd).not.toHaveBeenCalled();
  });

  it('calls onAdd and onCancel when a valid URL is submitted', async () => {
    const onAdd = vi.fn().mockResolvedValue(undefined);
    const onCancel = vi.fn();

    render(<AddLinkForm onAdd={onAdd} onCancel={onCancel} />);

    const input = screen.getByLabelText(/url/i);
    await typeUrl(input as HTMLInputElement, 'https://example.com');

    await userEvent.click(screen.getByRole('button', { name: /add link/i }));

    await waitFor(() => {
      expect(onAdd).toHaveBeenCalledWith('https://example.com');
      expect(onCancel).toHaveBeenCalled();
    });
  });

  it('shows error when onAdd rejects', async () => {
    const failingAdd = vi.fn().mockRejectedValue(new Error('boom'));

    render(<AddLinkForm onAdd={failingAdd} onCancel={vi.fn()} />);

    const input = screen.getByLabelText(/url/i);
    await typeUrl(input as HTMLInputElement, 'https://example.com');

    await userEvent.click(screen.getByRole('button', { name: /add link/i }));

    expect(await screen.findByText(/failed to add link/i)).toBeInTheDocument();
  });

  it('calls window.open when "Open in new tab" clicked', async () => {
    render(<AddLinkForm onAdd={vi.fn()} onCancel={vi.fn()} />);

    const input = screen.getByLabelText(/url/i);
    await typeUrl(input as HTMLInputElement, 'https://open-test.com');

    await userEvent.click(screen.getByRole('button', { name: /open in new tab/i }));

    expect((window.open as any)).toHaveBeenCalledWith(
      'https://open-test.com',
      '_blank',
      'noopener,noreferrer',
    );
  });
}); 
