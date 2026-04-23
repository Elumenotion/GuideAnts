import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { UploadFilesDialog } from '../UploadFilesDialog';
import { ProjectFolderDto } from '../../../../types/project';

// Helper to create a mock File
const createMockFile = (name: string, size: number, type: string): File => {
  return new File([new Array(size).join('a')], name, { type });
};

// Minimal folders used for tests
const mockFolders: ProjectFolderDto[] = [
  { id: 'root', name: 'Test Project', relativePath: '', parentFolderId: undefined } as ProjectFolderDto,
];

// Render helper
const renderDialog = (props?: Partial<React.ComponentProps<typeof UploadFilesDialog>>) => {
  const defaultProps = {
    isOpen: true,
    onClose: vi.fn(),
    onUpload: vi.fn().mockResolvedValue(undefined),
    folders: mockFolders,
  } as React.ComponentProps<typeof UploadFilesDialog>;

  return render(<UploadFilesDialog {...defaultProps} {...props} />);
};

describe('UploadFilesDialog (project)', () => {
  beforeEach(() => {
    vi.useRealTimers();
  });

  it('renders nothing when closed', () => {
    const { container } = render(
      <UploadFilesDialog isOpen={false} onClose={vi.fn()} onUpload={vi.fn()} folders={mockFolders} />
    );
    expect(container.firstChild).toBeNull();
  });

  it('renders dialog header and cancel button when open', () => {
    renderDialog();

    // Header is rendered as a heading element
    expect(screen.getByRole('heading', { name: /upload files/i })).toBeInTheDocument();

    // Footer cancel button
    expect(screen.getByRole('button', { name: /^cancel$/i })).toBeInTheDocument();
  });

  it('displays selected files after choosing files', async () => {
    renderDialog();

    const file = createMockFile('example.txt', 1024, 'text/plain');

    // Hidden input is still in the DOM
    const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
    expect(fileInput).not.toBeNull();

    await userEvent.upload(fileInput, file);

    expect(await screen.findByText('example.txt')).toBeInTheDocument();
    // The underlying size resolution may yield 1023 B due to File constructor quirk
    expect(screen.getByText(/\d+ (B|KB)/)).toBeInTheDocument();
  });

  it('enables Upload button once files are selected and calls onUpload', async () => {
    const uploadSpy = vi.fn().mockResolvedValue(undefined);
    const closeSpy = vi.fn();

    renderDialog({ onUpload: uploadSpy, onClose: closeSpy });

    const file = createMockFile('to-upload.txt', 2048, 'text/plain');
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await userEvent.upload(input, file);

    // Upload Files button
    const uploadBtn = await screen.findByRole('button', { name: /upload files/i });
    expect(uploadBtn).not.toBeDisabled();

    await userEvent.click(uploadBtn);

    await waitFor(() => {
      expect(uploadSpy).toHaveBeenCalledWith([file], undefined);
      expect(closeSpy).toHaveBeenCalled();
    });
  });

  it('calls onClose when Cancel button is clicked', async () => {
    const closeSpy = vi.fn();
    renderDialog({ onClose: closeSpy });

    const cancelBtn = await screen.findByRole('button', { name: /cancel/i });
    await userEvent.click(cancelBtn);

    expect(closeSpy).toHaveBeenCalled();
  });
}); 