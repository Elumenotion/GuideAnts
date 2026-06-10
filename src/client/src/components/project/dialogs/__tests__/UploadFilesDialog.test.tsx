import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { UploadFilesDialog } from '../UploadFilesDialog';
import { FolderTreeDto } from '../../../../types/project';

const createMockFile = (name: string, size: number, type: string): File => {
  return new File([new Array(size).join('a')], name, { type });
};

const folderTree: FolderTreeDto = {
  id: 'root',
  name: 'Project Root',
  relativePath: '',
  subFolders: [
    {
      id: 'folder-docs',
      name: 'Docs',
      relativePath: 'Docs',
      subFolders: [],
    },
  ],
};

const renderDialog = (props?: Partial<React.ComponentProps<typeof UploadFilesDialog>>) => {
  const defaultProps = {
    isOpen: true,
    onClose: vi.fn(),
    onUpload: vi.fn().mockResolvedValue(undefined),
    folderTree,
  } as React.ComponentProps<typeof UploadFilesDialog>;

  return render(<UploadFilesDialog {...defaultProps} {...props} />);
};

describe('UploadFilesDialog (project)', () => {
  beforeEach(() => {
    vi.useRealTimers();
  });

  it('renders nothing when closed', () => {
    const { container } = render(
      <UploadFilesDialog isOpen={false} onClose={vi.fn()} onUpload={vi.fn()} />
    );
    expect(container.firstChild).toBeNull();
  });

  it('renders dialog header and cancel button when open', () => {
    renderDialog();

    expect(screen.getByRole('heading', { name: /upload files/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^cancel$/i })).toBeInTheDocument();
  });

  it('displays selected files after choosing files', async () => {
    renderDialog();

    const file = createMockFile('example.txt', 1024, 'text/plain');
    const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
    expect(fileInput).not.toBeNull();

    await userEvent.upload(fileInput, file);

    expect(await screen.findByText('example.txt')).toBeInTheDocument();
    expect(screen.getByText(/\d+ (B|KB)/)).toBeInTheDocument();
  });

  it('enables Upload button once files are selected and calls onUpload', async () => {
    const uploadSpy = vi.fn().mockResolvedValue(undefined);
    const closeSpy = vi.fn();

    renderDialog({ onUpload: uploadSpy, onClose: closeSpy });

    const file = createMockFile('to-upload.txt', 2048, 'text/plain');
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await userEvent.upload(input, file);

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

    await userEvent.click(await screen.findByRole('button', { name: /cancel/i }));
    expect(closeSpy).toHaveBeenCalled();
  });

  it('accepts files from drag and drop', async () => {
    renderDialog();

    const file = createMockFile('dropped.txt', 512, 'text/plain');
    const dropZone = screen.getByText(/click to upload/i).closest('div')!;

    fireEvent.drop(dropZone, {
      dataTransfer: { files: [file] },
    });

    expect(await screen.findByText('dropped.txt')).toBeInTheDocument();
  });

  it('uploads to selected destination folder', async () => {
    const uploadSpy = vi.fn().mockResolvedValue(undefined);
    renderDialog({ onUpload: uploadSpy, initialFolderId: 'folder-docs' });

    const file = createMockFile('in-folder.txt', 256, 'text/plain');
    await userEvent.upload(document.querySelector('input[type="file"]') as HTMLInputElement, file);
    await userEvent.click(screen.getByRole('button', { name: /upload files/i }));

    await waitFor(() => {
      expect(uploadSpy).toHaveBeenCalledWith([file], 'folder-docs');
      expect(screen.getByText(/to Project Root\/Docs/i)).toBeInTheDocument();
    });
  });

  it('removes a selected file from the list', async () => {
    renderDialog();

    const file = createMockFile('remove-me.txt', 128, 'text/plain');
    await userEvent.upload(document.querySelector('input[type="file"]') as HTMLInputElement, file);
    expect(await screen.findByText('remove-me.txt')).toBeInTheDocument();

    const row = screen.getByText('remove-me.txt').closest('div')!;
    const removeButton = row.parentElement?.querySelector('button') as HTMLButtonElement;
    await userEvent.click(removeButton);

    expect(screen.queryByText('remove-me.txt')).not.toBeInTheDocument();
  });

  it('closes on Escape when not uploading', async () => {
    const onClose = vi.fn();
    renderDialog({ onClose });

    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });
});
