import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { SettingsButton } from '../SettingsButton';

const mockNavigate = vi.fn();
let mockPathname = '/projects/1';

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual('react-router-dom');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
    useLocation: () => ({ pathname: mockPathname }),
  };
});

describe('SettingsButton', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockPathname = '/projects/1';
  });

  it('navigates to settings when not already on settings page', async () => {
    render(<SettingsButton />);
    await userEvent.click(screen.getByRole('button', { name: 'Open Settings' }));
    expect(mockNavigate).toHaveBeenCalledWith('/settings');
  });

  it('does not navigate when already on settings page', async () => {
    mockPathname = '/settings';
    render(<SettingsButton />);
    const button = screen.getByRole('button', { name: 'Open Settings' });
    expect(button).toBeDisabled();
    await userEvent.click(button);
    expect(mockNavigate).not.toHaveBeenCalled();
  });
});
