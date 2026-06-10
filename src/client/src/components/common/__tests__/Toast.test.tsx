import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import { ToastProvider, useToast } from '../Toast';

vi.mock('react-dom', async () => {
  const actual = await vi.importActual('react-dom');
  return {
    ...actual,
    createPortal: (children: React.ReactNode) => children,
  };
});

function ToastHarness({ type = 'warning' as const }: { type?: 'success' | 'error' | 'info' | 'warning' }) {
  const { showToast } = useToast();
  return (
    <button
      type="button"
      onClick={() =>
        showToast({
          type,
          title: `${type} title`,
          message: `${type} message`,
          duration: 100,
        })
      }
    >
      Show {type} toast
    </button>
  );
}

describe('Toast', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows and auto-dismisses toasts', () => {
    render(
      <ToastProvider>
        <ToastHarness />
      </ToastProvider>
    );

    fireEvent.click(screen.getByRole('button', { name: 'Show warning toast' }));
    expect(screen.getByText('warning title')).toBeInTheDocument();
    expect(screen.getByText('warning message')).toBeInTheDocument();

    act(() => {
      vi.advanceTimersByTime(150);
    });
    expect(screen.queryByText('warning title')).not.toBeInTheDocument();
  });

  it('removes a toast when the close button is clicked', () => {
    render(
      <ToastProvider>
        <ToastHarness />
      </ToastProvider>
    );

    fireEvent.click(screen.getByRole('button', { name: 'Show warning toast' }));
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(screen.queryByText('warning title')).not.toBeInTheDocument();
  });

  it('renders default styling for an unknown toast type', () => {
    function UnknownTypeHarness() {
      const { showToast } = useToast();
      return (
        <button
          type="button"
          onClick={() =>
            showToast({
              type: 'unknown' as 'info',
              title: 'default title',
            })
          }
        >
          Show default toast
        </button>
      );
    }

    render(
      <ToastProvider>
        <UnknownTypeHarness />
      </ToastProvider>
    );

    fireEvent.click(screen.getByRole('button', { name: 'Show default toast' }));
    expect(screen.getByText('default title')).toBeInTheDocument();
  });

  it.each(['success', 'error', 'info'] as const)('renders %s toast styling', (type) => {
    render(
      <ToastProvider>
        <ToastHarness type={type} />
      </ToastProvider>
    );

    fireEvent.click(screen.getByRole('button', { name: `Show ${type} toast` }));
    expect(screen.getByText(`${type} title`)).toBeInTheDocument();
    expect(screen.getByText(`${type} message`)).toBeInTheDocument();
  });

  it('throws when useToast is used outside the provider', () => {
    const BrokenConsumer = () => {
      useToast();
      return null;
    };

    expect(() => render(<BrokenConsumer />)).toThrow('useToast must be used within a ToastProvider');
  });
});
