// TypeScript interfaces for the Project Details page
export interface NotebookDto {
    id: string;
    title: string;
}

export interface ContentFileDto {
    id: string;
    fileName: string;
    path: string;
}

export interface LinkDto {
    id: string;
    url: string;
}

export interface SemiStructuredProjectDataDto {
    id: string;
    placeholder: string;
}

export interface ProjectDetailsDto {
    id: string;
    title: string;
    description: string;
    created: string;
    notebooks: NotebookDto[];
    contentFiles: ContentFileDto[];
    links: LinkDto[];
    semiStructuredDatas: SemiStructuredProjectDataDto[];
}

// Add new types for handling file deletion conflicts
export interface FileUsageInNotebookDto {
  notebookId: string;
  notebookTitle: string;
  notebookFileId: string;
  fileName: string;
  relativePath: string;
}

export interface FileInUseErrorDto {
  message: string;
  notebooksUsingFile: FileUsageInNotebookDto[];
}

// Markdown extraction status enum - should match server-side enum
export enum MarkdownExtractionStatus {
  Pending = 'Pending',
  Processing = 'Processing',
  Completed = 'Completed',
  Failed = 'Failed',
  Skipped = 'Skipped'
}

// Markdown shadow DTOs - should match server-side DTOs
export interface ContentFileMarkdownShadowDto {
  id: string;
  originalContentFileVersionId: string;
  contentHash: string;
  fileSize: number;
  status: MarkdownExtractionStatus;
  errorMessage?: string;
  created: string;
  processedAt?: string;
}

export interface NotebookFileMarkdownShadowDto {
  id: string;
  originalNotebookFileId: string;
  contentHash: string;
  fileSize: number;
  status: MarkdownExtractionStatus;
  errorMessage?: string;
  created: string;
  processedAt?: string;
} 