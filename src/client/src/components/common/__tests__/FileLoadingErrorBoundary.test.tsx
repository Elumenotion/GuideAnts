import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FileLoadingErrorBoundary } from '../FileLoadingErrorBoundary';

function ThrowingChild({ shouldThrow }: { shouldThrow: boolean }) {
  if (shouldThrow) {
    throw new Error('load failed');
  }
  return <div>Loaded content</div>;
}

describe('FileLoadingErrorBoundary', () => {
  beforeEach(() => {
    vi.spyOn(console, 'log').mockImplementation(() => {});
  });

  it('renders children when no error occurs', () => {
    render(
      <FileLoadingErrorBoundary fileId="file-1" componentName="TestViewer">
        <ThrowingChild shouldThrow={false} />
      </FileLoadingErrorBoundary>
    );
    expect(screen.getByText('Loaded content')).toBeInTheDocument();
  });

  it('shows fallback UI with file id and retries after error', async () => {
    const gate = { shouldThrow: true };
    function GatedChild() {
      return <ThrowingChild shouldThrow={gate.shouldThrow} />;
    }

    render(
      <FileLoadingErrorBoundary fileId="file-99" componentName="PdfViewer">
        <GatedChild />
      </FileLoadingErrorBoundary>
    );

    expect(screen.getByText('Failed to load file')).toBeInTheDocument();
    expect(screen.getByText('File ID: file-99')).toBeInTheDocument();
    expect(console.log).toHaveBeenCalledWith(
      '[TELEMETRY] FileLoadingErrorBoundary caught error',
      expect.objectContaining({
        fileId: 'file-99',
        componentName: 'PdfViewer',
        error: 'load failed',
      })
    );

    gate.shouldThrow = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try Again' }));
    expect(screen.getByText('Loaded content')).toBeInTheDocument();
  });

  it('omits file id line when fileId prop is not provided', () => {
    render(
      <FileLoadingErrorBoundary componentName="ImageViewer">
        <ThrowingChild shouldThrow />
      </FileLoadingErrorBoundary>
    );

    expect(screen.getByText('Failed to load file')).toBeInTheDocument();
    expect(screen.queryByText(/File ID:/)).not.toBeInTheDocument();
  });
});
