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

/**
 * Prefix for the notebookFileId value that represents a path-only attachment
 * (host-mount files and folders that have no NotebookFile row). A pending
 * attachment whose id starts with this prefix is a path reference; anything
 * else is a real NotebookFile id. This is the single source of truth for the
 * id-vs-path invariant that both the undo restore path and the send path rely
 * on -- a real file id always round-trips by id, and the sentinel is reserved
 * for references with no id.
 */
export const PATH_SENTINEL_PREFIX = 'path:';

export function isPathSentinelId(id: string | null | undefined): boolean {
  return typeof id === 'string' && id.startsWith(PATH_SENTINEL_PREFIX);
}

/**
 * Build the wire reference for a pending attachment.
 *
 * Invariant (matches the restore path in useConversationActions): a pending
 * attachment that carries a real NotebookFile id is sent BY ID (relativePath is
 * dropped so the server expands it through the full image/text pipeline); a
 * path-sentinel attachment is sent BY PATH (notebookFileId is null). Keying off
 * the sentinel prefix -- not off the presence of relativePath -- is what lets a
 * restored image keep its id and reach the model as pixels.
 */
export function toSendAttachment(
  a: { notebookFileId: string; relativePath?: string; uploadType: UploadType },
): { notebookFileId: string | null; relativePath: string | null; uploadType: ServerUploadType } {
  if (isPathSentinelId(a.notebookFileId)) {
    const rawPath = a.relativePath ?? a.notebookFileId.slice(PATH_SENTINEL_PREFIX.length);
    return {
      notebookFileId: null,
      relativePath: normalizeRelativePath(rawPath),
      uploadType: uploadTypeToServer(a.uploadType),
    };
  }
  return {
    notebookFileId: a.notebookFileId,
    relativePath: null,
    uploadType: uploadTypeToServer(a.uploadType),
  };
}