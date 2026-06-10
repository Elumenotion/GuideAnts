import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { LinkContent } from '../LinkContent';
import '@testing-library/jest-dom';

// Helper link fixture
const sampleLink = {
  id: '1',
  url: 'https://example.com',
};

describe('LinkContent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('renders link details in view mode and triggers onStartEdit', async () => {
    const onStartEdit = vi.fn();

    render(
      <LinkContent
        link={sampleLink as any}
        canEdit
        onStartEdit={onStartEdit}
      />,
    );

    // link text visible
    expect(screen.getByText(sampleLink.url)).toBeInTheDocument();

    // start edit
    await userEvent.click(screen.getByRole('button', { name: /edit link/i }));
    expect(onStartEdit).toHaveBeenCalledTimes(1);
  });

  it('shows validation error when saving invalid URL in edit mode', async () => {
    const onUpdate = vi.fn();

    render(
      <LinkContent
        link={sampleLink as any}
        isEditing
        onUpdate={onUpdate}
      />,
    );

    const input = screen.getByPlaceholderText(/enter url/i);
    await userEvent.clear(input);
    await userEvent.type(input, 'invalid-url');

    await userEvent.click(screen.getByRole('button', { name: /save/i }));

    expect(await screen.findByText(/please enter a valid url/i)).toBeInTheDocument();
    expect(onUpdate).not.toHaveBeenCalled();
  });

  it('calls onUpdate with new URL when save succeeds', async () => {
    const onUpdate = vi.fn().mockResolvedValue(undefined);

    render(
      <LinkContent
        link={sampleLink as any}
        isEditing
        onUpdate={onUpdate}
      />,
    );

    const input = screen.getByPlaceholderText(/enter url/i);
    await userEvent.clear(input);
    await userEvent.type(input, 'https://new-url.com');

    await userEvent.click(screen.getByRole('button', { name: /save/i }));

    await waitFor(() => {
      expect(onUpdate).toHaveBeenCalledWith('1', { url: 'https://new-url.com' });
    });
  });

  it('calls onDelete after user confirms', async () => {
    const onDelete = vi.fn().mockResolvedValue(undefined);

    render(
      <LinkContent
        link={sampleLink as any}
        canEdit
        onDelete={onDelete}
      />,
    );

    // Open confirmation dialog
    await userEvent.click(screen.getByRole('button', { name: /delete/i }));

    // Click confirm in dialog
    const confirmBtn = await screen.findByTestId('confirm');
    await userEvent.click(confirmBtn);

    await waitFor(() => {
      expect(onDelete).toHaveBeenCalledWith('1');
    });
  });

  it('opens valid links through electron when available', async () => {
    const openExternal = vi.fn().mockResolvedValue(undefined);
    (window.electron as { openExternal?: typeof openExternal }).openExternal = openExternal;

    render(<LinkContent link={sampleLink as any} />);

    await userEvent.click(screen.getByRole('button', { name: sampleLink.url }));

    await waitFor(() => {
      expect(openExternal).toHaveBeenCalledWith(sampleLink.url);
    });
  });

  it('falls back to window.open when electron is unavailable', async () => {
    const electron = window.electron as { openExternal?: (url: string) => Promise<void> };
    const original = electron.openExternal;
    delete electron.openExternal;
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null);

    render(<LinkContent link={sampleLink as any} />);

    await userEvent.click(screen.getByRole('button', { name: sampleLink.url }));

    expect(openSpy).toHaveBeenCalledWith(sampleLink.url, '_blank', 'noopener,noreferrer');
    openSpy.mockRestore();
    electron.openExternal = original;
  });

  it('shows preview guidance for invalid URLs while editing', async () => {
    render(
      <LinkContent
        link={sampleLink as any}
        isEditing
        onUpdate={vi.fn()}
      />,
    );

    const input = screen.getByPlaceholderText(/enter url/i);
    await userEvent.clear(input);
    await userEvent.type(input, 'not-a-url');

    expect(screen.getByText(/enter a valid url starting with http/i)).toBeInTheDocument();
  });

  it('shows error when openExternal fails while editing', async () => {
    const openExternal = vi.fn().mockRejectedValue(new Error('blocked'));
    (window.electron as { openExternal?: typeof openExternal }).openExternal = openExternal;

    render(
      <LinkContent
        link={sampleLink as any}
        isEditing
        onUpdate={vi.fn()}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: /open in new tab/i }));

    expect(await screen.findByText(/failed to open link/i)).toBeInTheDocument();
  });

  it('copies the URL to the clipboard', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });

    render(<LinkContent link={sampleLink as any} />);

    await userEvent.click(screen.getByRole('button', { name: /copy url/i }));

    expect(writeText).toHaveBeenCalledWith(sampleLink.url);
  });

  it('cancels edit mode and restores the original URL', async () => {
    const onCancelEdit = vi.fn();

    render(
      <LinkContent
        link={sampleLink as any}
        isEditing
        onCancelEdit={onCancelEdit}
        onUpdate={vi.fn()}
      />,
    );

    const input = screen.getByPlaceholderText(/enter url/i);
    await userEvent.clear(input);
    await userEvent.type(input, 'https://changed.example');

    await userEvent.click(screen.getByRole('button', { name: /cancel/i }));

    expect(onCancelEdit).toHaveBeenCalled();
    expect(input).toHaveValue(sampleLink.url);
  });

  it('shows save error when update fails', async () => {
    const onUpdate = vi.fn().mockRejectedValue(new Error('network'));

    render(
      <LinkContent
        link={sampleLink as any}
        isEditing
        onUpdate={onUpdate}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: /save/i }));

    expect(await screen.findByText(/failed to update link/i)).toBeInTheDocument();
  });

  it('logs delete failures without leaving the dialog open', async () => {
    const onDelete = vi.fn().mockRejectedValue(new Error('network'));
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    render(
      <LinkContent
        link={sampleLink as any}
        canEdit
        onDelete={onDelete}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: /delete/i }));
    await userEvent.click(await screen.findByTestId('confirm'));

    await waitFor(() => {
      expect(onDelete).toHaveBeenCalledWith('1');
      expect(errorSpy).toHaveBeenCalled();
    });
    expect(screen.queryByTestId('confirm')).not.toBeInTheDocument();
    errorSpy.mockRestore();
  });

  it('dismisses delete confirmation without deleting', async () => {
    const onDelete = vi.fn();

    render(
      <LinkContent
        link={sampleLink as any}
        canEdit
        onDelete={onDelete}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: /delete/i }));
    await userEvent.click(screen.getByRole('button', { name: /cancel/i }));

    expect(onDelete).not.toHaveBeenCalled();
  });

  it('opens edited URL from preview when valid', async () => {
    const openExternal = vi.fn().mockResolvedValue(undefined);
    (window.electron as { openExternal?: typeof openExternal }).openExternal = openExternal;

    render(
      <LinkContent
        link={sampleLink as any}
        isEditing
        onUpdate={vi.fn()}
      />,
    );

    const input = screen.getByPlaceholderText(/enter url/i);
    await userEvent.clear(input);
    await userEvent.type(input, 'https://preview.example');

    await userEvent.click(screen.getByRole('button', { name: /open in new tab/i }));

    await waitFor(() => {
      expect(openExternal).toHaveBeenCalledWith('https://preview.example');
    });
  });
}); 
