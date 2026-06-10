import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import UserCell from '../UserCell';
import { render, screen } from '../../../../test/test-utils';

vi.mock('../ChatMarkdownViewer', () => ({
  default: ({ text, projectId, notebookId }: { text: string; projectId?: string; notebookId?: string }) => (
    <div data-testid="markdown-viewer" data-project={projectId} data-notebook={notebookId}>
      {text}
    </div>
  ),
}));

vi.mock('react-dom', async () => {
  const actual = await vi.importActual<typeof import('react-dom')>('react-dom');
  return {
    ...actual,
    createPortal: (node: React.ReactNode) => node,
  };
});

const makeAttachment = (id: string, name: string, size: number) => ({
  id,
  notebookFileId: `file-${id}`,
  fileName: name,
  fileType: 'application/octet-stream',
  fileSize: size,
  uploadedAt: new Date(),
  status: 'complete' as const,
});

describe('UserCell – attachments & fullscreen viewer', () => {
  it('renders multiple attachment pills with sizes', () => {
    render(
      <UserCell
        content="Files attached"
        isLast={false}
        attachments={[
          makeAttachment('1', 'alpha.pdf', 1024),
          makeAttachment('2', 'beta.txt', 512),
        ]}
        onPreviewFile={vi.fn()}
      />
    );

    expect(screen.getByText('alpha.pdf')).toBeInTheDocument();
    expect(screen.getByText('beta.txt')).toBeInTheDocument();
    expect(screen.getAllByText(/\(1KB\)/)).toHaveLength(2);
  });

  it('omits size label when fileSize is zero', () => {
    render(
      <UserCell
        content="No size"
        isLast={false}
        attachments={[makeAttachment('3', 'empty.bin', 0)]}
      />
    );
    expect(screen.getByText('empty.bin')).toBeInTheDocument();
    expect(screen.queryByText(/\(\d+KB\)/)).not.toBeInTheDocument();
  });

  it('passes projectId and notebookId to markdown viewer in fullscreen', async () => {
    const user = userEvent.setup();
    render(
      <UserCell
        content="Scoped content"
        isLast={true}
        projectId="proj-1"
        notebookId="nb-2"
      />
    );

    await user.click(screen.getByLabelText('Full screen'));

    const viewer = screen.getByTestId('markdown-viewer');
    expect(viewer).toHaveAttribute('data-project', 'proj-1');
    expect(viewer).toHaveAttribute('data-notebook', 'nb-2');
    expect(viewer).toHaveTextContent('Scoped content');
  });

  it('shows fullscreen button only on last message', () => {
    const { rerender } = render(<UserCell content="Mid" isLast={false} />);
    expect(screen.queryByLabelText('Full screen')).not.toBeInTheDocument();

    rerender(<UserCell content="Last" isLast={true} />);
    expect(screen.getByLabelText('Full screen')).toBeInTheDocument();
  });

  it('does not call onPreviewFile when handler is omitted', async () => {
    const user = userEvent.setup();
    render(
      <UserCell
        content="No handler"
        isLast={false}
        attachments={[makeAttachment('4', 'orphan.dat', 100)]}
      />
    );
    await user.click(screen.getByTitle('Click to preview orphan.dat'));
    // No throw; click is a no-op without handler
    expect(screen.getByText('orphan.dat')).toBeInTheDocument();
  });
});
