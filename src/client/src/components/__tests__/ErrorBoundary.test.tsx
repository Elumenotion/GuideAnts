import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '../../test/test-utils';
import ErrorBoundary from '../ErrorBoundary';
import userEvent from '@testing-library/user-event';

/**
 * Utility component that throws when rendered to simulate a rendering failure.
 */
const ProblemChild = () => {
  throw new Error('Problem!');
};

describe('ErrorBoundary', () => {
  // Silence expected console.error noise from React error logging
  beforeEach(() => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    (console.error as unknown as { mockRestore: () => void }).mockRestore();
  });

  it('renders children when no error occurs', () => {
    render(
      <ErrorBoundary>
        <div data-testid="safe-child">Safe Child</div>
      </ErrorBoundary>
    );

    expect(screen.getByTestId('safe-child')).toBeInTheDocument();
  });

  it('renders provided fallback UI when an error is caught', () => {
    const Fallback = () => <div data-testid="custom-fallback">Fallback UI</div>;
    const fallbackFn = () => <Fallback />;

    render(
      <ErrorBoundary fallback={fallbackFn}>
        {/* This child throws on render */}
        <ProblemChild />
      </ErrorBoundary>
    );

    expect(screen.getByTestId('custom-fallback')).toBeInTheDocument();
  });

  it('renders default ErrorScreen when no fallback is provided', () => {
    render(
      <ErrorBoundary>
        {/* This child throws on render */}
        <ProblemChild />
      </ErrorBoundary>
    );

    // Default ErrorScreen shows a generic heading
    expect(screen.getByText('Something went wrong')).toBeInTheDocument();
    // And a retry button
    expect(screen.getByRole('button', { name: /try again/i })).toBeInTheDocument();
  });

  it('invokes window.location.reload when the retry button is clicked', async () => {
    const reloadSpy = vi.fn();
    Object.defineProperty(window, 'location', {
      value: { reload: reloadSpy },
      writable: true
    });

    render(
      <ErrorBoundary>
        <ProblemChild />
      </ErrorBoundary>
    );

    const retryButton = screen.getByRole('button', { name: /try again/i });
    await userEvent.click(retryButton);

    expect(reloadSpy).toHaveBeenCalled();
  });
}); 