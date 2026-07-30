import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { HomeButton } from '../HomeButton';

const mockNavigate = vi.fn();

vi.mock('react-router', async () => {
  const actual = await vi.importActual('react-router');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

describe('HomeButton', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('navigates to home when clicked', async () => {
    render(<HomeButton />);
    await userEvent.click(screen.getByRole('button', { name: 'Back to Home' }));
    expect(mockNavigate).toHaveBeenCalledWith('/');
  });

  it('applies optional className', () => {
    render(<HomeButton className="extra-class" />);
    expect(screen.getByRole('button', { name: 'Back to Home' })).toHaveClass('extra-class');
  });
});
