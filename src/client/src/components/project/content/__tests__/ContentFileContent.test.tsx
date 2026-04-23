import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';

// -----------------------------------------------------------------------------
// Local component under test & test-doubles
// -----------------------------------------------------------------------------
import { ContentFileContent } from '../ContentFileContent';

// Stub FileContents to avoid nested fetches/logic – we only verify it renders
// a placeholder so ContentFileContent can mount without hitting the network.
vi.mock('../FileContents', () => ({
  FileContents: () => <div data-testid="stub-file-contents">Stub File Contents</div>,
}));

// Stub HistoryDrawer to avoid ProjectContext dependency
vi.mock('../../history/HistoryDrawer', () => ({
  HistoryDrawer: () => <div data-testid="stub-history-drawer" />,
}));

// -----------------------------------------------------------------------------
// Shared API mocks – maintained inside a single object to avoid hoisting issues.
// -----------------------------------------------------------------------------
vi.mock('../../../../services/api', () => {
  const mocks = {
    getContentFile: vi.fn(),
    deleteContentFile: vi.fn(),
    getContentFileContent: vi.fn(),
  } as const;
  // Expose for tests via globalThis to avoid hoisting/TDZ issues
  (globalThis as any).__apiMocks = mocks;
  return {
    api: {
      projects: mocks,
      utils: {},
    },
  };
});

// Helper accessors after mocks initialised
const apiMocks = () => (globalThis as any).__apiMocks as {
  getContentFile: ReturnType<typeof vi.fn>;
  deleteContentFile: ReturnType<typeof vi.fn>;
};

const getContentFile = () => apiMocks().getContentFile;
const deleteContentFile = () => apiMocks().deleteContentFile;

// Re-usable DTO fixture
const baseFileDto = {
  id: 'f1',
  name: 'docs/readme.md',
  contentType: 'text/markdown',
  fileName: 'readme.md',
  index: false,
  documentId: '',
  created: new Date().toISOString(),
};

// Convenience helpers
const PROJECT_ID = 'p1';
const FILE_ID = 'f1';

// -----------------------------------------------------------------------------
// Tests
// -----------------------------------------------------------------------------

describe('ContentFileContent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('deletes the file after user confirmation', async () => {
    getContentFile().mockResolvedValueOnce(baseFileDto);
    deleteContentFile().mockResolvedValueOnce(undefined);

    const onDelete = vi.fn();

    render(
      <ContentFileContent
        projectId={PROJECT_ID}
        fileId={FILE_ID}
        canEdit
        onDelete={onDelete}
      />,
    );

    // Wait for file name to appear then click delete
    await screen.findByText(baseFileDto.fileName);

    await userEvent.click(screen.getByRole('button', { name: /delete/i }));

    // Click confirm in dialog
    const confirmButton = await screen.findByTestId('confirm');
    await userEvent.click(confirmButton);

    await waitFor(() => {
      expect(deleteContentFile()).toHaveBeenCalledWith(PROJECT_ID, FILE_ID);
      expect(onDelete).toHaveBeenCalledTimes(1);
    });
  });

  it('shows an error message when fetching file details fails', async () => {
    getContentFile().mockRejectedValueOnce(new Error('Fetch failed'));

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);

    expect(
      await screen.findByText(/failed to load file details\. please try again\./i),
    ).toBeInTheDocument();
  });
}); 