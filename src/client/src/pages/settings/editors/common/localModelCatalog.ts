import type { LocalModelCatalogEntryDto, LocalModelCatalogResponseDto } from '../../../../types/settings';

export type LocalModelCatalogServiceId = 'Embeddings' | 'SpeechTranscription' | 'SpeechSynthesis';

export function parseLocalModelCatalog(payload: unknown): LocalModelCatalogEntryDto[] {
  if (!payload || typeof payload !== 'object') {
    return [];
  }
  const entries = (payload as LocalModelCatalogResponseDto).entries;
  if (!Array.isArray(entries)) {
    return [];
  }
  return entries.filter(
    (entry): entry is LocalModelCatalogEntryDto =>
      Boolean(entry && typeof entry === 'object' && typeof entry.id === 'string' && entry.id.trim())
  );
}

export function formatLocalModelCatalogLabel(entry: LocalModelCatalogEntryDto): string {
  const name = entry.displayName?.trim() || entry.id;
  const parts: string[] = [name];
  if (typeof entry.producedDimension === 'number') {
    parts.push(`${entry.producedDimension}-dim`);
  }
  if (entry.license?.trim()) {
    parts.push(entry.license.trim());
  }
  if (entry.default) {
    parts.push('default');
  }
  if (parts.length === 1) {
    return name;
  }
  return `${name} (${parts.slice(1).join(', ')})`;
}

export function sortLocalModelCatalogEntries(entries: LocalModelCatalogEntryDto[]): LocalModelCatalogEntryDto[] {
  return [...entries].sort((left, right) => {
    if (left.default !== right.default) {
      return left.default ? -1 : 1;
    }
    return formatLocalModelCatalogLabel(left).localeCompare(formatLocalModelCatalogLabel(right));
  });
}
