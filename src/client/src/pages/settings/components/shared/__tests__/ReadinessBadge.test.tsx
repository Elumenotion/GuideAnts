import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ReadinessBadge } from '../ReadinessBadge';

describe('ReadinessBadge', () => {
  it('renders ready status with default label', () => {
    render(<ReadinessBadge status="ready" />);
    expect(screen.getByText('ready')).toBeInTheDocument();
  });

  it('renders not-configured status with friendly label', () => {
    render(<ReadinessBadge status="not-configured" />);
    expect(screen.getByText('Not configured')).toBeInTheDocument();
  });

  it('renders blocked status', () => {
    render(<ReadinessBadge status="blocked" />);
    expect(screen.getByText('blocked')).toBeInTheDocument();
  });

  it('uses custom label when provided', () => {
    render(<ReadinessBadge status="ready" label="Configured" />);
    expect(screen.getByText('Configured')).toBeInTheDocument();
  });

  it('falls back to generic styling for unknown status', () => {
    const { container } = render(<ReadinessBadge status="pending" />);
    expect(screen.getByText('pending')).toBeInTheDocument();
    expect(container.firstChild).toHaveClass('bg-gray-100');
  });
});
