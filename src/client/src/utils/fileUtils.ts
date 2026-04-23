// Supported file types for Microsoft Kernel Memory indexing
// Based on official documentation - excluding OCR image formats since OCR is not implemented
export const INDEXABLE_FILE_EXTENSIONS = [
    // Plain text and markup
    '.txt', '.md', '.json', '.html', '.htm', '.rtf',
    // Microsoft Office documents
    '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx',
    // PDF documents
    '.pdf'
];

/**
 * Checks if a file can be indexed by Kernel Memory based on its extension
 * @param fileName The name of the file including extension
 * @returns true if the file type is supported for indexing
 */
export function isFileIndexable(fileName: string): boolean {
    if (!fileName) return false;
    
    const extension = fileName.toLowerCase().split('.').pop();
    return extension ? INDEXABLE_FILE_EXTENSIONS.includes(`.${extension}`) : false;
}

/**
 * Gets the file extension from a filename
 * @param fileName The name of the file
 * @returns The file extension with dot (e.g., '.pdf') or empty string if no extension
 */
export function getFileExtension(fileName: string): string {
    if (!fileName) return '';
    
    const parts = fileName.split('.');
    return parts.length > 1 ? `.${parts.pop()?.toLowerCase()}` : '';
}

/**
 * Gets a human-readable list of supported file types for display in UI
 * @returns Formatted string of supported extensions
 */
export function getSupportedFileTypesDisplay(): string {
    return INDEXABLE_FILE_EXTENSIONS.join(', ');
}

/**
 * Determines the MIME content type from a file name based on its extension
 * @param fileName The name of the file including extension
 * @returns The MIME content type string
 */
export function getContentTypeFromFileName(fileName: string): string {
    const extension = fileName.split('.').pop()?.toLowerCase();
    const contentTypeMap: Record<string, string> = {
        // Images
        'jpg': 'image/jpeg',
        'jpeg': 'image/jpeg',
        'png': 'image/png',
        'gif': 'image/gif',
        'svg': 'image/svg+xml',
        'webp': 'image/webp',
        
        // Documents
        'pdf': 'application/pdf',
        'doc': 'application/msword',
        'docx': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        'xls': 'application/vnd.ms-excel',
        'xlsx': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
        'ppt': 'application/vnd.ms-powerpoint',
        'pptx': 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
        
        // Text
        'txt': 'text/plain',
        'md': 'text/markdown',
        'html': 'text/html',
        'css': 'text/css',
        'js': 'application/javascript',
        'ts': 'application/typescript',
        'json': 'application/json',
        'xml': 'application/xml',
        'csv': 'text/csv',
        
        // Archives
        'zip': 'application/zip',
        'rar': 'application/x-rar-compressed',
        '7z': 'application/x-7z-compressed',
        
        // Code files
        'py': 'text/x-python',
        'java': 'text/x-java',
        'cpp': 'text/x-c++src',
        'c': 'text/x-csrc',
        'php': 'application/x-httpd-php',
        'rb': 'text/x-ruby',
        'go': 'text/x-go',
        'rs': 'text/x-rust',
        
        // Other
        'mp3': 'audio/mpeg',
        'mp4': 'video/mp4',
        'avi': 'video/x-msvideo'
    };
    
    return contentTypeMap[extension || ''] || 'application/octet-stream';
}

/**
 * Formats a file size in bytes to a human-readable string
 * @param bytes The file size in bytes
 * @returns Formatted file size string (e.g., "1.5 MB")
 */
export function formatFileSize(bytes: number): string {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
} 