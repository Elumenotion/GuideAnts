export type UploadType = 'image' | 'audio' | 'text' | 'other';

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

export function uploadTypeToServer(uploadType: UploadType): 'ImageFile' | 'AudioFile' | 'TextFile' | 'SandboxFile' {
  switch (uploadType) {
    case 'image':
      return 'ImageFile';
    case 'audio':
      return 'AudioFile';
    case 'text':
      return 'TextFile';
    default:
      return 'SandboxFile';
  }
} 