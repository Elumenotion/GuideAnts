export type UploadType = 'image' | 'audio' | 'text' | 'folder' | 'other';
export type ServerUploadType = 'ImageFile' | 'ImageUrl' | 'AudioFile' | 'TextFile' | 'SandboxFile' | 'Folder';

/**
 * Return simplified upload type from a filename extension
 */
export function mapContentType(fileName: string): UploadType {
  const ext = fileName.split('.').pop()?.toLowerCase() ?? '';
  if (['png', 'jpg', 'jpeg', 'gif', 'bmp', 'tiff', 'webp'].includes(ext)) return 'image';
  if (['wav', 'mp3', 'flac', 'aac', 'ogg', 'm4a'].includes(ext)) return 'audio';
  if (['txt', 'md', 'csv', 'json', 'xml', 'html', 'htm', 'css', 'js', 'ts', 'tsx', 'py', 'cs', 'java', 'c', 'cpp', 'go', 'rs'].includes(ext)) return 'text';
  return 'other';
}

export function uploadTypeToServer(uploadType: UploadType): 'ImageFile' | 'AudioFile' | 'TextFile' | 'SandboxFile' | 'Folder' {
  switch (uploadType) {
    case 'image':
      return 'ImageFile';
    case 'audio':
      return 'AudioFile';
    case 'text':
      return 'TextFile';
    case 'folder':
      return 'Folder';
    case 'other':
      return 'SandboxFile';
  }
}

/**
 * Normalize notebook-relative paths before comparing or persisting them.
 * The original casing is intentionally preserved for display fidelity.
 */
export function normalizeRelativePath(relativePath: string): string {
  return relativePath.replace(/\\/g, '/').trim().replace(/^\/+/, '');
}

/**
 * Convert a server upload enum back to the pending-chip representation.
 * Null/undefined is reserved for legacy rows that predate UploadType persistence.
 */
export function toPendingUploadType(
  uploadType: ServerUploadType | null | undefined,
  fileName: string,
): UploadType {
  if (uploadType == null) {
    return mapContentType(fileName);
  }

  switch (uploadType) {
    case 'ImageFile':
    case 'ImageUrl':
      return 'image';
    case 'AudioFile':
      return 'audio';
    case 'TextFile':
      return 'text';
    case 'Folder':
      return 'folder';
    case 'SandboxFile':
      return 'other';
    default:
      throw new Error(`Unsupported server upload type: ${uploadType}`);
  }
}

export function fileTypeFromUploadType(uploadType: UploadType): 'image' | 'audio' | 'text' | 'folder' | 'other' {
  return uploadType;
}