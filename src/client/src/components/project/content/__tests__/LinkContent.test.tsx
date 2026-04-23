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
}); 
