import { useRef } from 'react';
import { FaFile, FaCode, FaTimes, FaPlus, FaEye, FaSpinner, FaExclamationTriangle, FaCheckCircle, FaDownload } from 'react-icons/fa';
import { FileUploadDto, FileDto, MarkdownExtractionStatus } from '../../../types/guides';
import { isMarkdownExtractionSupported, SUPPORTED_MARKDOWN_EXTRACTION_EXTENSIONS } from '../../../utils/fileTypeSupport';
import { isMaterializableSkillPayloadPath } from './executablePayload';

interface FilesProps {
  existingFiles: FileDto[]; // Files already on the server
  newFiles: FileUploadDto[]; // Files to be uploaded
  onExistingFilesChange: (files: FileDto[]) => void;
  onNewFilesChange: (files: FileUploadDto[]) => void;
  onPreviewMarkdown?: (fileId: string) => void; // Callback for preview button
  onDownloadFile?: (fileId: string, fileName: string) => void; // Callback for download button
}

export function Files({ existingFiles, newFiles, onExistingFilesChange, onNewFilesChange, onPreviewMarkdown, onDownloadFile }: FilesProps) {
  const vectorStoreInputRef = useRef<HTMLInputElement>(null);
  const codeInterpreterInputRef = useRef<HTMLInputElement>(null);

  // Status badge component for VectorStore files
  const StatusBadge = ({ file }: { file: FileDto }) => {
    if (!file.markdownShadow) {
      return (
        <span className="text-xs text-gray-400 bg-gray-100 px-2 py-0.5 rounded flex items-center gap-1">
          <FaSpinner className="w-3 h-3 animate-spin" />
          Pending
        </span>
      );
    }

    const { status, errorMessage } = file.markdownShadow;

    switch (status) {
      case MarkdownExtractionStatus.Pending:
        return (
          <span className="text-xs text-gray-600 bg-gray-100 px-2 py-0.5 rounded flex items-center gap-1">
            <FaSpinner className="w-3 h-3" />
            Queued
          </span>
        );
      case MarkdownExtractionStatus.Processing:
        return (
          <span className="text-xs text-blue-600 bg-blue-100 px-2 py-0.5 rounded flex items-center gap-1">
            <FaSpinner className="w-3 h-3 animate-spin" />
            Processing
          </span>
        );
      case MarkdownExtractionStatus.Completed:
        return (
          <span className="text-xs text-green-600 bg-green-100 px-2 py-0.5 rounded flex items-center gap-1">
            <FaCheckCircle className="w-3 h-3" />
            Ready
          </span>
        );
      case MarkdownExtractionStatus.Failed:
        return (
          <span 
            className="text-xs text-red-600 bg-red-100 px-2 py-0.5 rounded flex items-center gap-1 cursor-help"
            title={errorMessage || 'Extraction failed'}
          >
            <FaExclamationTriangle className="w-3 h-3" />
            Failed
          </span>
        );
      case MarkdownExtractionStatus.Skipped:
        return (
          <span 
            className="text-xs text-orange-600 bg-orange-100 px-2 py-0.5 rounded flex items-center gap-1 cursor-help"
            title={errorMessage || 'File type not supported for extraction'}
          >
            <FaExclamationTriangle className="w-3 h-3" />
            Unsupported
          </span>
        );
      default:
        return null;
    }
  };

  const handleFileUpload = async (file: File, folderKind: string, vectorStoreName?: string): Promise<FileUploadDto | null> => {
    try {
      // Validate file type for VectorStore
      if (folderKind === 'VectorStore' && !isMarkdownExtractionSupported(file.name, file.type)) {
        alert(
          `File type not supported for content extraction.\n\n` +
          `Supported types: PDF, Word (.docx), Excel (.xlsx), PowerPoint (.pptx), ` +
          `Images (JPEG, PNG, BMP, TIFF, HEIF), Text files (.txt, .md, .html, .json, .xml, .csv, .tsv)\n\n` +
          `File: ${file.name}`
        );
        return null;
      }

      const arrayBuffer = await file.arrayBuffer();
      const uint8Array = new Uint8Array(arrayBuffer);
      
      // Convert to base64 string (what the backend expects)
      // Using chunked conversion to avoid call stack size exceeded error on large files
      let binaryString = '';
      const chunkSize = 8192; // Process 8KB at a time
      for (let i = 0; i < uint8Array.length; i += chunkSize) {
        const chunk = uint8Array.subarray(i, i + chunkSize);
        binaryString += String.fromCharCode(...chunk);
      }
      const base64 = btoa(binaryString);
      
      const fileUpload: FileUploadDto = {
        folderKind,
        vectorStoreName,
        relativePath: file.name,
        contentBytes: base64,
        contentType: file.type,
      };

      return fileUpload;
    } catch (error) {
      console.error('File upload error:', error);
      alert(`Failed to upload file: ${error instanceof Error ? error.message : 'Unknown error'}`);
      return null;
    }
  };

  const handleMultipleFileUpload = async (files: File[], folderKind: string, vectorStoreName?: string) => {
    // Process all files first
    const fileUploadPromises = files.map(file => handleFileUpload(file, folderKind, vectorStoreName));
    const fileUploads = await Promise.all(fileUploadPromises);
    
    // Filter out null results (failed uploads) and add all successful uploads at once
    const successfulUploads = fileUploads.filter((upload): upload is FileUploadDto => upload !== null);
    
    if (successfulUploads.length > 0) {
      onNewFilesChange([...newFiles, ...successfulUploads]);
    }
  };

  const handleRemoveExistingFile = (fileId: string) => {
    onExistingFilesChange(existingFiles.filter(f => f.id !== fileId));
  };

  const handleRemoveNewFile = (index: number) => {
    onNewFilesChange(newFiles.filter((_, i) => i !== index));
  };

  // Combine existing and new files for display
  const existingVectorStore = existingFiles.filter((f) => f.folderKind === 'VectorStore');
  const newVectorStore = newFiles.filter((f) => f.folderKind === 'VectorStore');
  const existingCodeInterpreter = existingFiles.filter((f) => f.folderKind === 'CodeInterpreter');
  const newCodeInterpreter = newFiles.filter((f) => f.folderKind === 'CodeInterpreter');
  const existingSkillOrigin = existingFiles.filter(
    (f) => f.folderKind === 'Skill',
  );
  const newSkillOrigin = newFiles.filter((f) => f.folderKind === 'Skill');
  const existingNotebookFiles = [...existingCodeInterpreter, ...existingSkillOrigin];
  const newNotebookFiles = [...newCodeInterpreter, ...newSkillOrigin];

  return (
    <div className="space-y-6" data-tour-id="guide.files.section">
      <h2 className="text-lg font-semibold text-gray-900">Files</h2>
      
      {/* Assistant Reference Files */}
      <div className="space-y-4">
        <h3 className="text-sm font-medium text-gray-700">Assistant Reference Files</h3>
        <p className="text-xs text-gray-500">
          These files contain information for the guide/assistant which it can read when needed. They allow access to more information than is convenient for the assistant's instructions, or information it only needs some of the time.
        </p>

        {(existingVectorStore.length > 0 || newVectorStore.length > 0) && (
          <div className="space-y-2">
            {/* Existing files from server */}
            {existingVectorStore.map((file) => (
              <div key={file.id} className="flex items-center justify-between p-3 bg-purple-50 border border-purple-200 rounded-md">
                <div className="flex items-center gap-2 flex-1">
                  <FaFile className="w-5 h-5 text-purple-600" />
                  <span className="text-sm font-medium text-gray-900">{file.relativePath}</span>
                  {file.vectorStoreName && (
                    <span className="text-xs text-purple-700 bg-purple-100 px-2 py-0.5 rounded">
                      {file.vectorStoreName}
                    </span>
                  )}
                  <StatusBadge file={file} />
                </div>
                <div className="flex items-center gap-2">
                  {file.markdownShadow?.status === MarkdownExtractionStatus.Completed && onPreviewMarkdown && (
                    <button
                      type="button"
                      onClick={() => onPreviewMarkdown(file.id)}
                      className="text-purple-600 hover:text-purple-700 p-1"
                      title="Preview extracted content"
                    >
                      <FaEye className="w-4 h-4" />
                    </button>
                  )}
                  <button
                    type="button"
                    onClick={() => handleRemoveExistingFile(file.id)}
                    className="text-red-600 hover:text-red-700 p-1"
                    title="Remove"
                  >
                    <FaTimes className="w-4 h-4" />
                  </button>
                </div>
              </div>
            ))}
            {/* New files to upload */}
            {newVectorStore.map((file, index) => (
              <div key={`new-${index}`} className="flex items-center justify-between p-3 bg-purple-100 border border-purple-300 rounded-md border-dashed">
                <div className="flex items-center gap-2">
                  <FaFile className="w-5 h-5 text-purple-600" />
                  <span className="text-sm font-medium text-gray-900">{file.relativePath}</span>
                  {file.vectorStoreName && (
                    <span className="text-xs text-purple-700 bg-purple-200 px-2 py-0.5 rounded">
                      {file.vectorStoreName}
                    </span>
                  )}
                  <span className="text-xs text-orange-600 ml-2">(pending upload)</span>
                </div>
                <button
                  type="button"
                  onClick={() => handleRemoveNewFile(newFiles.indexOf(file))}
                  className="text-red-600 hover:text-red-700 p-1"
                  title="Remove"
                >
                  <FaTimes className="w-4 h-4" />
                </button>
              </div>
            ))}
          </div>
        )}

        <button
          type="button"
          onClick={() => vectorStoreInputRef.current?.click()}
          className="flex items-center gap-2 px-4 py-2 text-sm text-purple-600 border border-purple-300 rounded-md hover:bg-purple-50"
          data-tour-id="guide.files.vectorStores.upload"
        >
          <FaPlus className="w-4 h-4" />
          Upload Reference File
        </button>

        <input
          ref={vectorStoreInputRef}
          type="file"
          multiple
          accept={SUPPORTED_MARKDOWN_EXTRACTION_EXTENSIONS.join(',')}
          onChange={(e) => {
            const files = Array.from(e.target.files || []);
            if (files.length > 0) {
              handleMultipleFileUpload(files, 'VectorStore', 'default');
            }
            e.target.value = '';
          }}
          className="hidden"
        />
      </div>

      {/* Code Interpreter Files */}
      <div className="space-y-4" data-tour-id="guide.files.codeInterpreter.section">
        <h3 className="text-sm font-medium text-gray-700">Notebook Files</h3>
        <p className="text-xs text-gray-500">
          These files are copied into notebooks based on the guide and its crew members. They can be used as attachments and memories in chats and accessed, analyzed, and modified by code the assistant writes (e.g., data files, spreadsheets, documents to process).
        </p>

        {(existingNotebookFiles.length > 0 || newNotebookFiles.length > 0) && (
          <div className="space-y-2">
            {/* Existing files from server */}
            {existingNotebookFiles.map((file) => (
              <div key={file.id} className="flex items-center justify-between p-3 bg-green-50 border border-green-200 rounded-md">
                <div className="flex items-center gap-2">
                  <FaCode className="w-5 h-5 text-green-600" />
                  <span className="text-sm font-medium text-gray-900">{file.relativePath}</span>
                  {file.folderKind === 'Skill' && isMaterializableSkillPayloadPath(file.relativePath) && (
                    <span className="text-xs text-green-700 bg-green-100 px-2 py-0.5 rounded">skill payload</span>
                  )}
                  <span className="text-xs text-gray-500 ml-2">(saved)</span>
                </div>
                <div className="flex items-center gap-2">
                  {onDownloadFile && (
                    <button
                      type="button"
                      onClick={() => onDownloadFile(file.id, file.relativePath)}
                      className="text-green-600 hover:text-green-700 p-1"
                      title="Download"
                    >
                      <FaDownload className="w-4 h-4" />
                    </button>
                  )}
                  <button
                    type="button"
                    onClick={() => handleRemoveExistingFile(file.id)}
                    className="text-red-600 hover:text-red-700 p-1"
                    title="Remove"
                  >
                    <FaTimes className="w-4 h-4" />
                  </button>
                </div>
              </div>
            ))}
            {/* New files to upload */}
            {newNotebookFiles.map((file, index) => (
              <div key={`new-${index}`} className="flex items-center justify-between p-3 bg-green-100 border border-green-300 rounded-md border-dashed">
                <div className="flex items-center gap-2">
                  <FaCode className="w-5 h-5 text-green-600" />
                  <span className="text-sm font-medium text-gray-900">{file.relativePath}</span>
                  <span className="text-xs text-orange-600 ml-2">(pending upload)</span>
                </div>
                <button
                  type="button"
                  onClick={() => handleRemoveNewFile(newFiles.indexOf(file))}
                  className="text-red-600 hover:text-red-700 p-1"
                  title="Remove"
                >
                  <FaTimes className="w-4 h-4" />
                </button>
              </div>
            ))}
          </div>
        )}

        <button
          type="button"
          onClick={() => codeInterpreterInputRef.current?.click()}
          className="flex items-center gap-2 px-4 py-2 text-sm text-green-600 border border-green-300 rounded-md hover:bg-green-50"
          data-tour-id="guide.files.codeInterpreter.upload"
        >
          <FaPlus className="w-4 h-4" />
          Upload File
        </button>

        <input
          ref={codeInterpreterInputRef}
          type="file"
          multiple
          onChange={(e) => {
            const files = Array.from(e.target.files || []);
            if (files.length > 0) {
              handleMultipleFileUpload(files, 'CodeInterpreter');
            }
            e.target.value = '';
          }}
          className="hidden"
        />
      </div>
    </div>
  );
}


