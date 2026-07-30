import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import '@testing-library/jest-dom';
import CliAuthorize from '../CliAuthorize';

const mockApproveSession = vi.fn();
const mockDenySession = vi.fn();

vi.mock('../../services/api', () => ({
  api: {
    cli: {
      approveSession: (...args: unknown[]) => mockApproveSession(...args),
      denySession: (...args: unknown[]) => mockDenySession(...args),
    },
  },
}));

function renderPage(search = '') {
  return render(
    <MemoryRouter initialEntries={[`/cli/authorize${search}`]}>
      <Routes>
        <Route path="/cli/authorize" element={<CliAuthorize />} />
        <Route path="/" element={<div>home-page</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('CliAuthorize', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows an error when the session query param is missing', () => {
    renderPage();

    expect(screen.getByText('Invalid Link')).toBeInTheDocument();
    expect(
      screen.getByText('This authorization link is missing its session identifier.'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /approve/i })).not.toBeInTheDocument();
  });

  it('calls api.cli.approveSession with the session id on Approve and shows success', async () => {
    mockApproveSession.mockResolvedValueOnce(undefined);
    const user = userEvent.setup();

    renderPage('?session=test-session-123');

    expect(screen.getByText('Authorize command-line mount access?')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /approve/i }));

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Approved' })).toBeInTheDocument();
    });

    expect(mockApproveSession).toHaveBeenCalledTimes(1);
    expect(mockApproveSession).toHaveBeenCalledWith('test-session-123');
    expect(screen.getByText('Approved — this window will close automatically.')).toBeInTheDocument();
  });

  it('shows an error state when the approve call fails with 404', async () => {
    const error: any = new Error('Not Found');
    error.status = 404;
    mockApproveSession.mockRejectedValueOnce(error);
    const user = userEvent.setup();

    renderPage('?session=expired-session');

    await user.click(screen.getByRole('button', { name: /approve/i }));

    await waitFor(() => {
      expect(
        screen.getByText('This request is no longer valid. Return to your terminal and start over.'),
      ).toBeInTheDocument();
    });
  });

  it('shows an error state when the approve call fails with 410', async () => {
    const error: any = new Error('Gone');
    error.status = 410;
    mockApproveSession.mockRejectedValueOnce(error);
    const user = userEvent.setup();

    renderPage('?session=gone-session');

    await user.click(screen.getByRole('button', { name: /approve/i }));

    await waitFor(() => {
      expect(
        screen.getByText('This request is no longer valid. Return to your terminal and start over.'),
      ).toBeInTheDocument();
    });
  });

  it('shows a generic error when the approve call fails with another status', async () => {
    const error: any = new Error('Internal Server Error');
    error.status = 500;
    mockApproveSession.mockRejectedValueOnce(error);
    const user = userEvent.setup();

    renderPage('?session=bad-session');

    await user.click(screen.getByRole('button', { name: /approve/i }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
      expect(screen.getByText('Internal Server Error')).toBeInTheDocument();
    });
  });

  it('calls api.cli.denySession and shows the denied state when Deny is clicked', async () => {
    mockDenySession.mockResolvedValueOnce(undefined);
    const user = userEvent.setup();

    renderPage('?session=some-session');

    await user.click(screen.getByRole('button', { name: /deny/i }));

    await waitFor(() => {
      expect(screen.getByText('Request Denied')).toBeInTheDocument();
    });
    expect(screen.getByText('Request denied — this window will close automatically.')).toBeInTheDocument();
    expect(mockDenySession).toHaveBeenCalledTimes(1);
    expect(mockDenySession).toHaveBeenCalledWith('some-session');
    expect(mockApproveSession).not.toHaveBeenCalled();
  });
});
