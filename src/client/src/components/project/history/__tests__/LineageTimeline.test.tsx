import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '../../../../test/test-utils';
import { LineageTimeline } from '../LineageTimeline';
import { FileLineageEvent } from '../../../../types/fileLineage';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import * as downloadModule from '../../../../hooks/useDownloadLineage';

// Stub the hook to control state
vi.mock('../../../../hooks/useDownloadLineage', () => {
  return {
    useDownloadLineage: () => ({
      download: vi.fn(),
      isDownloading: false,
    }),
  };
});

const makeEvent = (partial: Partial<FileLineageEvent>): FileLineageEvent => ({
  id: 'e1',
  action: 'Uploaded',
  fileKind: 'Project',
  fileId: 'f1',
  projectId: 'p1',
  fileName: 'readme.md',
  timestamp: new Date().toISOString(),
  userId: 'u1',
  userDisplayName: 'User One',
  versionNumber: 1,
  ...partial,
});

describe('LineageTimeline', () => {
  it('shows placeholder when no events', () => {
    render(<LineageTimeline events={[]} canDownload={false} />);
    expect(screen.getByText(/no history available/i)).toBeInTheDocument();
  });

  it('renders events and triggers download when allowed', async () => {
    const events = [makeEvent({ id: 'e1' }), makeEvent({ id: 'e2', action: 'Versioned', versionNumber: 2 })];
    const downloadSpy = vi.fn();
    vi.spyOn(downloadModule, 'useDownloadLineage').mockReturnValue({
      download: downloadSpy,
      isDownloading: false,
    });

    render(<LineageTimeline events={events} canDownload={true} />);

    // Two list items
    expect(screen.getAllByRole('listitem')).toHaveLength(2);

    // Click download on first event
    await userEvent.click(screen.getAllByRole('button')[0]);
    expect(downloadSpy).toHaveBeenCalledWith('e1', 'readme.md');
  });

  it('displays notebook name when available', () => {
    const events = [
      makeEvent({ 
        id: 'e1', 
        action: 'CopiedToNotebook', 
        notebookName: 'Data Analysis Notebook',
        notebookId: 'nb1',
        projectId: 'p1'
      })
    ];
    
    render(<LineageTimeline events={events} canDownload={false} />);
    
    expect(screen.getByText('📓 Data Analysis Notebook')).toBeInTheDocument();
    expect(screen.getByText('CopiedToNotebook')).toBeInTheDocument();
  });

  it('makes notebook name clickable for navigation', async () => {
    const events = [
      makeEvent({ 
        id: 'e1', 
        action: 'CopiedToNotebook', 
        notebookName: 'Data Analysis Notebook',
        notebookId: 'nb1',
        projectId: 'p1'
      })
    ];
    
    render(<LineageTimeline events={events} canDownload={false} />);
    
    const notebookButton = screen.getByRole('button', { name: /📓 Data Analysis Notebook/i });
    expect(notebookButton).toBeInTheDocument();
    expect(notebookButton).toHaveAttribute('title', 'Open Data Analysis Notebook notebook');
  });
}); 