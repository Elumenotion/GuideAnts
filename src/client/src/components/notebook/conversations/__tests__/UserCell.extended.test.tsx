import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import UserCell from '../UserCell';
import { render, screen } from '../../../../test/test-utils';

vi.mock('../ChatMarkdownViewer', () => ({
  default: ({ text }: { text: string }) => <div data-testid="markdown-viewer">{text}</div>,
}));

vi.mock('react-dom', async () => {
  const actual = await vi.importActual<typeof import('react-dom')>('react-dom');
  return {
    ...actual,
    createPortal: (node: React.ReactNode) => node,
  };
});

describe('UserCell – attachments & preview', () => {
  it('renders attachment pills with file size', () => {
    render(
      <UserCell
        content="Message with files"
        isLast={false}
        attachments={[
          {
            id: 'att-1',
            notebookFileId: 'file-1',
            fileName: 'report.pdf',
            fileType: 'application/pdf',
            fileSize: 2048,
            uploadedAt: new Date(),
            status: 'complete',
          },
        ]}
      />
    );

    expect(screen.getByText('report.pdf')).toBeInTheDocument();
    expect(screen.getByText('(2KB)')).toBeInTheDocument();
  });

  it('calls onPreviewFile when attachment clicked', async () => {
    const user = userEvent.setup();
    const onPreviewFile = vi.fn();

    render(
      <UserCell
        content="See attachment"
        isLast={false}
        attachments={[
          {
            id: 'att-2',
            notebookFileId: 'file-99',
            fileName: 'notes.md',
            fileType: 'text/markdown',
            fileSize: 0,
            uploadedAt: new Date(),
            status: 'complete',
          },
        ]}
        onPreviewFile={onPreviewFile}
      />
    );

    await user.click(screen.getByTitle('Click to preview notes.md'));
    expect(onPreviewFile).toHaveBeenCalledWith('file-99');
  });

  it('does not render attachment section when list is empty', () => {
    render(
      <UserCell
        content="No files"
        isLast={false}
        attachments={[]}
      />
    );
    expect(screen.queryByText('📎')).not.toBeInTheDocument();
  });
});
