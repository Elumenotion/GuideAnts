import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';

// Mock third-party and internal dependencies that are not critical for this test
vi.mock('@azure/msal-react', () => ({
  MsalProvider: ({ children }: { children: React.ReactNode }) => <div data-testid="msal-provider">{children}</div>,
}));
vi.mock('react-dnd', () => ({
  DndProvider: ({ children }: { children: React.ReactNode }) => <div data-testid="dnd-provider">{children}</div>,
}));
vi.mock('react-dnd-html5-backend', () => ({ HTML5Backend: {} }));

// Stub internal UI pieces to keep the render tree small
vi.mock('../components/AppContent', () => ({ default: () => <div data-testid="app-content" /> }));
vi.mock('../components/ZoomControl', () => ({ default: () => <div data-testid="zoom-control" /> }));
vi.mock('../components/LoadingSpinner', () => ({ default: () => <div data-testid="loading-spinner" /> }));
vi.mock('../components/ErrorBoundary', () => ({ default: ({ children }: { children: React.ReactNode }) => <>{children}</> }));
vi.mock('../hooks/useMermaidCleanup', () => ({ useMermaidCleanup: () => {} }));

// Mock authService behaviour
vi.mock('../services/authService', () => ({
  authService: {
    initialize: vi.fn().mockResolvedValue(undefined),
    getMsalInstance: vi.fn().mockReturnValue({}),
  },
}));

import App from '../App';

describe('App component', () => {
  it('initializes and renders inner content', async () => {
    render(<App />);

    // Loading spinner should render first
    expect(screen.getByTestId('loading-spinner')).toBeInTheDocument();

    // Wait for async initialize to resolve and content to appear
    expect(await screen.findByTestId('app-content')).toBeInTheDocument();
    expect(screen.getByTestId('zoom-control')).toBeInTheDocument();
  });
}); 