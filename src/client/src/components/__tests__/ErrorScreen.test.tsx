import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import '@testing-library/jest-dom';
import { vi, describe, it, expect } from 'vitest';
import ErrorScreen, { NetworkErrorScreen, AuthErrorScreen, ServerErrorScreen } from '../ErrorScreen';

const renderWithRouter = (component: React.ReactElement) => {
  return render(
    <MemoryRouter>
      {component}
    </MemoryRouter>
  );
};

describe('ErrorScreen', () => {
  it('renders with custom title and message', () => {
    renderWithRouter(<ErrorScreen title="Custom Error" message="Custom message" />);
    
    expect(screen.getByRole('heading', { name: 'Custom Error' })).toBeInTheDocument();
    expect(screen.getByText('Custom message')).toBeInTheDocument();
  });

  it('renders with error object', () => {
    const error = new Error('Test error message');
    renderWithRouter(<ErrorScreen error={error} />);
    
    expect(screen.getByRole('heading', { name: 'Connection Error' })).toBeInTheDocument();
    expect(screen.getByText('Test error message', { selector: 'p.text-gray-600' })).toBeInTheDocument();
  });

  it('calls onRetry when retry button is clicked', async () => {
    const onRetry = vi.fn();
    renderWithRouter(<ErrorScreen onRetry={onRetry} />);
    
    const retryButton = screen.getByRole('button', { name: 'Try Again' });
    fireEvent.click(retryButton);
    
    expect(onRetry).toHaveBeenCalled();
  });

  it('shows loading state during retry', async () => {
    const onRetry = vi.fn().mockImplementation(() => new Promise(resolve => setTimeout(resolve, 100)));
    renderWithRouter(<ErrorScreen onRetry={onRetry} />);
    
    const retryButton = screen.getByRole('button', { name: 'Try Again' });
    fireEvent.click(retryButton);
    
    expect(retryButton).toBeDisabled();
    await waitFor(() => {
      expect(retryButton).not.toBeDisabled();
    });
  });

  it('renders custom actions when provided', () => {
    const customActions = <button>Custom Action</button>;
    renderWithRouter(<ErrorScreen customActions={customActions} />);
    
    expect(screen.getByRole('button', { name: 'Custom Action' })).toBeInTheDocument();
  });

  it('shows technical details when error is provided', () => {
    const error = new Error('Technical error details');
    renderWithRouter(<ErrorScreen error={error} />);
    
    const detailsToggle = screen.getByText('Technical Details');
    fireEvent.click(detailsToggle);
    
    expect(screen.getByText('Technical error details', { selector: 'div.text-gray-700' })).toBeInTheDocument();
  });

  it('shows correct icon for network errors', () => {
    renderWithRouter(<ErrorScreen error={new Error('Failed to fetch')} />);
    expect(screen.getByText('📡')).toBeInTheDocument();
  });

  it('shows correct icon for authentication errors', () => {
    renderWithRouter(<ErrorScreen error={new Error('Unauthorized')} />);
    expect(screen.getByText('🔒')).toBeInTheDocument();
  });

  it('shows correct icon for timeout errors', () => {
    renderWithRouter(<ErrorScreen error={new Error('timeout')} />);
    expect(screen.getByText('⏱️')).toBeInTheDocument();
  });

  it('translates "failed to fetch" to user-friendly message', () => {
    renderWithRouter(<ErrorScreen error={new Error('Failed to fetch')} />);
    expect(screen.getByText('Unable to connect to the server. Please check your internet connection and try again.')).toBeInTheDocument();
  });

  it('navigates to login when retry is clicked on auth errors', () => {
    renderWithRouter(<ErrorScreen error={new Error('Unauthorized access')} />);

    fireEvent.click(screen.getByRole('button', { name: 'Sign In' }));
    expect(screen.getByRole('button', { name: 'Sign In' })).toBeInTheDocument();
  });

  it('reloads page when retry has no handler', () => {
    const reload = vi.fn();
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...window.location, reload },
    });

    renderWithRouter(<ErrorScreen error={new Error('Generic failure')} />);
    fireEvent.click(screen.getByRole('button', { name: 'Try Again' }));
    expect(reload).toHaveBeenCalled();
  });

  it('calls onBack when back button is clicked', () => {
    const onBack = vi.fn();
    renderWithRouter(<ErrorScreen onBack={onBack} />);

    fireEvent.click(screen.getByRole('button', { name: 'Back to Home' }));
    expect(onBack).toHaveBeenCalled();
  });

  it('hides retry and back buttons when configured off', () => {
    renderWithRouter(
      <ErrorScreen showRetryButton={false} showBackButton={false} />
    );

    expect(screen.queryByRole('button', { name: 'Try Again' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Back to Home' })).not.toBeInTheDocument();
  });

  it('shows generic warning icon for unknown errors', () => {
    renderWithRouter(<ErrorScreen error={new Error('Something unexpected')} />);
    expect(screen.getByText('⚠️')).toBeInTheDocument();
  });

  it('translates network errors to friendly copy', () => {
    renderWithRouter(<ErrorScreen error={new Error('network error occurred')} />);
    expect(
      screen.getByText('A network error occurred. Please check your connection and try again.')
    ).toBeInTheDocument();
  });

  it('translates authentication token errors', () => {
    renderWithRouter(<ErrorScreen error={new Error('No authentication token provided')} />);
    expect(
      screen.getByText('Authentication failed. Please sign in again to continue.')
    ).toBeInTheDocument();
  });

  it('translates timeout errors', () => {
    renderWithRouter(<ErrorScreen error={new Error('Request timeout exceeded')} />);
    expect(
      screen.getByText('The request timed out. The server might be busy, please try again in a moment.')
    ).toBeInTheDocument();
  });
});

describe('NetworkErrorScreen', () => {
  it('renders network error screen with correct defaults', () => {
    renderWithRouter(<NetworkErrorScreen />);
    
    expect(screen.getByText('Connection Problem')).toBeInTheDocument();
    expect(screen.getByText('Unable to connect to the server. Please check your internet connection and try again.')).toBeInTheDocument();
  });
});

describe('AuthErrorScreen', () => {
  it('renders auth error screen with sign in button', () => {
    renderWithRouter(<AuthErrorScreen />);
    
    expect(screen.getByText('Authentication Required')).toBeInTheDocument();
    expect(screen.getByText('Please sign in to continue using the application.')).toBeInTheDocument();
    expect(screen.getByText('Sign In')).toBeInTheDocument();
    expect(screen.queryByText('Try Again')).not.toBeInTheDocument();
  });
});

describe('ServerErrorScreen', () => {
  it('renders server error screen with correct message', () => {
    renderWithRouter(<ServerErrorScreen />);
    
    expect(screen.getByText('Server Error')).toBeInTheDocument();
    expect(screen.getByText('The server is currently experiencing issues. Please try again in a few moments.')).toBeInTheDocument();
  });
}); 