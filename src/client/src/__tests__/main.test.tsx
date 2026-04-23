import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import { waitFor } from '@testing-library/react';

// Mock App component to keep it lightweight
vi.mock('../App', () => ({ default: () => <div data-testid="root-app">App Loaded</div> }));

// Ensure there is a root div for ReactDOM to mount into
beforeEach(() => {
  document.body.innerHTML = '<div id="root"></div>';
});

describe('main entry file', () => {
  it('renders App into the root element without crashing', async () => {
    await import('../main'); // executes the module side-effects (ReactDOM.render)

    await waitFor(() => {
      const rootDiv = document.getElementById('root');
      expect(rootDiv).toBeInTheDocument();
      expect(rootDiv).toHaveTextContent('App Loaded');
    });
  });
}); 