// Utility functions for markdown extraction and audio transcription functionality

// Supported file extensions based on markdown extraction and speech service capabilities
// These should match the server-side supported extensions
const SUPPORTED_EXTENSIONS = new Set([
    // Markdown extraction supported formats
    '.pdf', '.docx', '.xlsx', '.pptx', '.html',
    '.jpg', '.jpeg', '.png', '.bmp', '.tiff', '.heif',
    // Speech Service supported audio formats
    '.wav', '.mp3', '.ogg', '.flac', '.wma', '.aac', '.amr', '.webm', '.opus',
    // Video formats (audio extraction)
    '.mp4', '.mov', '.avi', '.webm', '.wmv', '.mkv', '.flv', '.m4v'
]);

// Supported content types
const SUPPORTED_CONTENT_TYPES = new Set([
    // Markdown extraction supported content types
    'application/pdf',
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    'application/vnd.openxmlformats-officedocument.presentationml.presentation',
    'text/html',
    'application/xhtml+xml',
    'image/jpeg',
    'image/jpg', 
    'image/png',
    'image/bmp',
    'image/tiff',
    'image/heif',
    // Speech Service supported audio content types
    'audio/wav',
    'audio/wave',
    'audio/mpeg',
    'audio/mp3',
    'audio/mp4',
    'audio/aac',
    'audio/ogg',
    'audio/flac',
    'audio/x-ms-wma',
    'audio/amr',
    'audio/webm',
    'audio/opus',
    // Video content types (for audio extraction)
    'video/mp4',
    'video/quicktime',
    'video/x-msvideo',
    'video/webm',
    'video/x-ms-wmv',
    'video/x-matroska'
]);

/**
 * Checks if a file type is supported for markdown extraction or audio transcription
 * @param fileName - The name of the file (to extract extension)
 * @param contentType - The MIME type of the file
 * @returns true if the file type supports markdown extraction (documents/images) or transcription (audio/video)
 */
export function isMarkdownExtractionSupported(fileName: string, contentType: string): boolean {
    if (!fileName) {
        return false;
    }

    const extension = getFileExtension(fileName);
    const isExtensionSupported = SUPPORTED_EXTENSIONS.has(extension);
    
    // If we have content type, also check that
    const isContentTypeSupported = !contentType || SUPPORTED_CONTENT_TYPES.has(contentType);
    
    return isExtensionSupported && isContentTypeSupported;
}

/**
 * Gets the file extension from a filename in lowercase
 * @param fileName - The filename to extract extension from
 * @returns The file extension in lowercase (including the dot)
 */
function getFileExtension(fileName: string): string {
    const lastDotIndex = fileName.lastIndexOf('.');
    if (lastDotIndex === -1) {
        return '';
    }
    return fileName.substring(lastDotIndex).toLowerCase();
}

/**
 * Gets a human-readable description of supported file types for markdown extraction and transcription
 * @returns A formatted string describing supported file types
 */
export function getSupportedFileTypesDescription(): string {
    return 'PDF, Word documents (DOCX), Excel files (XLSX), PowerPoint (PPTX), HTML files, images (JPG, PNG, BMP, TIFF, HEIF), and audio/video files (MP3, WAV, MP4, OGG, FLAC, WMA, AAC, AMR, WebM, OPUS, MOV, AVI, WMV, MKV, FLV, M4V)';
} 
