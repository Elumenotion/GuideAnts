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

/**
 * Collapse leading `../` segments used by CWD-relative turn paths
 * (private notebooks CWD = Output/, so `../file.rttm` means notebook-root `file.rttm`).
 */
export function resolveCwdRelativeNotebookPath(path: string, cwdBase = 'Output'): string {
  const normalized = normalizeNotebookRelativePath(path);
  if (!normalized) {
    return '';
  }

  const baseParts = cwdBase
    .split('/')
    .map(part => part.trim())
    .filter(Boolean);
  const resolved: string[] = [...baseParts];

  for (const segment of normalized.split('/')) {
    if (!segment || segment === '.') {
      continue;
    }
    if (segment === '..') {
      if (resolved.length > 0) {
        resolved.pop();
      }
      continue;
    }
    resolved.push(segment);
  }

  return resolved.join('/');
}

export function getNotebookPathCandidates(path: string): string[] {
  const normalized = normalizeNotebookRelativePath(path);
  if (!normalized) {
    return [];
  }

  const candidates = new Set<string>();
  candidates.add(normalized);

  // Turn pills often use CWD-relative paths including `../` for notebook-root writes.
  if (normalized.startsWith('../') || normalized.includes('/../')) {
    const fromOutput = resolveCwdRelativeNotebookPath(normalized, 'Output');
    if (fromOutput) {
      candidates.add(fromOutput);
    }
    const fromRuns = resolveCwdRelativeNotebookPath(normalized, 'Runs');
    if (fromRuns) {
      candidates.add(fromRuns);
    }
  }

  const outputMatch = normalized.match(/^output\/(.+)$/i);
  if (outputMatch) {
    candidates.add(outputMatch[1]);
    candidates.add(`Output/${outputMatch[1]}`);
  } else if (!normalized.startsWith('../')) {
    candidates.add(`Output/${normalized}`);
  }

  return Array.from(candidates);
}

export function notebookPathMatches(candidatePath: string, requestedPath: string): boolean {
  const normalizedCandidate = normalizeNotebookRelativePath(candidatePath).toLowerCase();
  if (!normalizedCandidate) {
    return false;
  }

  if (getNotebookPathCandidates(requestedPath)
    .some(pathCandidate => normalizeNotebookRelativePath(pathCandidate).toLowerCase() === normalizedCandidate)) {
    return true;
  }

  const normalizedRequested = normalizeNotebookRelativePath(requestedPath).toLowerCase();
  if (!normalizedRequested) {
    return false;
  }

  // CWD-relative paths from published runs (e.g. "duck.png" -> "Runs/{runId}/duck.png")
  if (normalizedCandidate === normalizedRequested) {
    return true;
  }

  return normalizedCandidate.endsWith(`/${normalizedRequested}`);
}
