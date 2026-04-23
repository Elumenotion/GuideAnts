// Supported MIME types for markdown extraction
export const SUPPORTED_MARKDOWN_EXTRACTION_MIMES = [
  'application/pdf',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document', // .docx
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', // .xlsx
  'application/vnd.openxmlformats-officedocument.presentationml.presentation', // .pptx
  'image/jpeg',
  'image/png',
  'image/bmp',
  'image/tiff',
  'image/heif',
  'text/plain',
  'text/markdown',
  'text/html',
  'application/json',
  'application/xml',
  'text/csv',
  'text/tab-separated-values',
];

// Supported file extensions for markdown extraction
export const SUPPORTED_MARKDOWN_EXTRACTION_EXTENSIONS = [
  '.pdf',
  '.docx',
  '.xlsx',
  '.pptx',
  '.jpg',
  '.jpeg',
  '.png',
  '.bmp',
  '.tiff',
  '.heif',
  '.txt',
  '.md',
  '.html',
  '.json',
  '.xml',
  '.csv',
  '.tsv',
];

/**
 * Check if a file is supported for markdown extraction based on filename and content type
 */
export function isMarkdownExtractionSupported(fileName: string, contentType?: string): boolean {
  // Check by content type first
  if (contentType && SUPPORTED_MARKDOWN_EXTRACTION_MIMES.includes(contentType.toLowerCase())) {
    return true;
  }

  // Check by file extension
  const extension = `.${fileName.split('.').pop()?.toLowerCase()}`;
  return SUPPORTED_MARKDOWN_EXTRACTION_EXTENSIONS.includes(extension);
}
