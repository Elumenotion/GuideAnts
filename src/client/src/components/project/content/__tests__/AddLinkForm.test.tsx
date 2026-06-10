import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
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
    (window as unknown as { electron?: unknown }).electron = undefined;
  });

  afterEach(() => {
    vi.restoreAllMocks();
    (window as unknown as { electron?: unknown }).electron = undefined;
  });

  it('disables submit when URL is empty', () => {
    render(<AddLinkForm onAdd={vi.fn()} onCancel={vi.fn()} />);
    expect(screen.getByRole('button', { name: /add link/i })).toBeDisabled();
  });

  it('shows validation error when submitting invalid URL', async () => {
    const onAdd = vi.fn();
    render(<AddLinkForm onAdd={onAdd} onCancel={vi.fn()} />);

    // Enter invalid URL and attempt submit
    const input = screen.getByLabelText(/url/i);
    await typeUrl(input as HTMLInputElement, 'invalid-url');
    await userEvent.click(screen.getByRole('button', { name: /add link/i }));

    expect(onAdd).not.toHaveBeenCalled();
    expect(await screen.findByText(/valid url starting with http/i)).toBeInTheDocument();
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

  it('uses electron openExternal when available', async () => {
    const openExternal = vi.fn().mockResolvedValue(undefined);
    (window as unknown as { electron: { openExternal: typeof openExternal } }).electron = {
      openExternal,
    };

    render(<AddLinkForm onAdd={vi.fn()} onCancel={vi.fn()} />);
    const input = screen.getByLabelText(/url/i);
    await typeUrl(input as HTMLInputElement, 'https://electron.example');

    await userEvent.click(screen.getByRole('button', { name: /open in new tab/i }));
    expect(openExternal).toHaveBeenCalledWith('https://electron.example');
  });

  it('calls window.open when electron is unavailable', async () => {
    render(<AddLinkForm onAdd={vi.fn()} onCancel={vi.fn()} />);

    const input = screen.getByLabelText(/url/i);
    await typeUrl(input as HTMLInputElement, 'https://open-test.com');

    await userEvent.click(screen.getByRole('button', { name: /open in new tab/i }));

    expect(window.open).toHaveBeenCalledWith(
      'https://open-test.com',
      '_blank',
      'noopener,noreferrer',
    );
  });
});
