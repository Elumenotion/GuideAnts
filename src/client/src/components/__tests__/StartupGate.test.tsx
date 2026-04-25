import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import StartupGate from '../StartupGate';

describe('StartupGate', () => {
  it('renders children immediately when disabled', () => {
    render(
      <StartupGate enabled={false}>
        <div data-testid="ready-content" />
      </StartupGate>
    );

    expect(screen.getByTestId('ready-content')).toBeInTheDocument();
  });

  it('waits for the startup endpoint to report ready', async () => {
    const fetchMock = vi
      .spyOn(window, 'fetch')
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ status: 'starting' }), {
          status: 503,
          headers: { 'Content-Type': 'application/json' },
        })
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ status: 'ready' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      );

    render(
      <StartupGate enabled pollIntervalMs={5}>
        <div data-testid="ready-content" />
      </StartupGate>
    );

    expect(screen.getByText('Starting GuideAnts...')).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByTestId('ready-content')).toBeInTheDocument();
    });

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
