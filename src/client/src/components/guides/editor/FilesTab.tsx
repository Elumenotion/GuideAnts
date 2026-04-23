import React from 'react';
import { Files } from './Files';
import { FileUploadDto, FileDto } from '../../../types/guides';

interface FilesTabProps {
  existingFiles: FileDto[];
  newFiles: FileUploadDto[];
  onExistingFilesChange: (files: FileDto[]) => void;
  onNewFilesChange: (files: FileUploadDto[]) => void;
  onPreviewMarkdown?: (fileId: string) => void;
  onDownloadFile?: (fileId: string, fileName: string) => void;
}

const FilesTabComponent = ({
  existingFiles,
  newFiles,
  onExistingFilesChange,
  onNewFilesChange,
  onPreviewMarkdown,
  onDownloadFile,
}: FilesTabProps) => {
  return (
    <Files 
      existingFiles={existingFiles}
      newFiles={newFiles}
      onExistingFilesChange={onExistingFilesChange}
      onNewFilesChange={onNewFilesChange}
      onPreviewMarkdown={onPreviewMarkdown}
      onDownloadFile={onDownloadFile}
    />
  );
};

export const FilesTab = React.memo(FilesTabComponent, (prevProps, nextProps) => {
  // Only re-render if file arrays actually change (by reference or length)
  if (prevProps.existingFiles !== nextProps.existingFiles) return false;
  if (prevProps.newFiles !== nextProps.newFiles) return false;
  // Callbacks should be stable (memoized)
  return true;
});

