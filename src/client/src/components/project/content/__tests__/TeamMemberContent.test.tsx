import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';

import { ProjectMemberContent } from '../ProjectMemberContent';

// -----------------------------------------------------------------------------
// Fixtures & helpers
// -----------------------------------------------------------------------------
const sampleMember = {
  userId: 'u1',
  userName: 'Alice',
  userEmail: 'alice@example.com',
  roleName: 'Contributor',
};

function setupComponent(overrideProps: Partial<React.ComponentProps<typeof ProjectMemberContent>> = {}) {
  const props = {
    member: sampleMember as any,
    canEdit: false,
    isCurrentUserOwner: false,
    onUpdate: vi.fn(),
    onDelete: vi.fn(),
    ...overrideProps,
  } as React.ComponentProps<typeof ProjectMemberContent>;

  render(<ProjectMemberContent {...props} />);
  return props;
}

// -----------------------------------------------------------------------------
// Tests
// -----------------------------------------------------------------------------

describe('ProjectMemberContent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders member information and hides edit actions when editing not allowed', () => {
    setupComponent({ canEdit: false });

    expect(screen.getByText(sampleMember.userName)).toBeInTheDocument();
    expect(screen.getByText(`Email: ${sampleMember.userEmail}`)).toBeInTheDocument();
    expect(screen.getByText(/role: contributor/i)).toBeInTheDocument();
    // Edit/remove buttons should not be present
    expect(screen.queryByRole('button', { name: /change role/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /remove from project/i })).not.toBeInTheDocument();
  });

  it('allows project owner to change a contributor role', async () => {
    const onUpdate = vi.fn().mockResolvedValue(undefined);
    setupComponent({ canEdit: true, isCurrentUserOwner: true, onUpdate });

    await userEvent.click(screen.getByRole('button', { name: /change role/i }));

    // select element should appear
    const select = screen.getByRole('combobox');
    await userEvent.selectOptions(select, 'Reader');

    const saveBtn = screen.getByRole('button', { name: /save/i });
    expect(saveBtn).not.toBeDisabled();

    await userEvent.click(saveBtn);

    await waitFor(() => {
      expect(onUpdate).toHaveBeenCalledWith('u1', 'Reader');
    });
  });

  it('shows error message when updateRole fails', async () => {
    const onUpdate = vi.fn().mockRejectedValue(new Error('fail'));
    setupComponent({ canEdit: true, isCurrentUserOwner: true, onUpdate });

    await userEvent.click(screen.getByRole('button', { name: /change role/i }));
    await userEvent.selectOptions(screen.getByRole('combobox'), 'Reader');
    await userEvent.click(screen.getByRole('button', { name: /save/i }));

    expect(await screen.findByText(/failed to update role/i)).toBeInTheDocument();
  });

  it('removes member after confirmation', async () => {
    const onDelete = vi.fn().mockResolvedValue(undefined);

    setupComponent({ canEdit: true, isCurrentUserOwner: true, onDelete });

    await userEvent.click(screen.getByRole('button', { name: /remove from project/i }));

    // Click confirm in the dialog
    const confirmBtn = await screen.findByTestId('confirm');
    await userEvent.click(confirmBtn);

    await waitFor(() => {
      expect(onDelete).toHaveBeenCalledWith('u1');
    });
  });
}); 