function safelyDecodeUriPath(path: string): string {
  try {
    return decodeURIComponent(path);
  } catch {
    return path;
  }
}

export function normalizeNotebookRelativePath(path: string): string {
  const decoded = safelyDecodeUriPath(path.trim());
  return decoded
    .replace(/\\/g, '/')
    .replace(/^\.\/+/, '')
    .replace(/^\/+/, '')
    .replace(/\/{2,}/g, '/');
}

export function getNotebookPathCandidates(path: string): string[] {
  const normalized = normalizeNotebookRelativePath(path);
  if (!normalized) {
    return [];
  }

  const candidates = new Set<string>();
  candidates.add(normalized);

  const outputMatch = normalized.match(/^output\/(.+)$/i);
  if (outputMatch) {
    candidates.add(outputMatch[1]);
    candidates.add(`Output/${outputMatch[1]}`);
  } else {
    candidates.add(`Output/${normalized}`);
  }

  return Array.from(candidates);
}

export function notebookPathMatches(candidatePath: string, requestedPath: string): boolean {
  const normalizedCandidate = normalizeNotebookRelativePath(candidatePath).toLowerCase();
  if (!normalizedCandidate) {
    return false;
  }

  return getNotebookPathCandidates(requestedPath)
    .some(pathCandidate => normalizeNotebookRelativePath(pathCandidate).toLowerCase() === normalizedCandidate);
}
