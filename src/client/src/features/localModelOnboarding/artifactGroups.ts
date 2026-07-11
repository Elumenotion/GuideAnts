import type { HuggingFaceRepositoryFileDto } from '../../types/settings';

export interface ArtifactGroup {
  id: string;
  label: string;
  files: string[];
  totalBytes: number;
  sharded: boolean;
  shardCount: number;
}

function normalizeQuantLabel(file: HuggingFaceRepositoryFileDto): string {
  if (file.quantLabel?.trim()) {
    return file.quantLabel.trim().toUpperCase();
  }
  const base = file.path.split('/').pop() ?? file.path;
  return base.replace(/\.gguf$/i, '').toUpperCase();
}

function shardGroupKey(file: HuggingFaceRepositoryFileDto): string {
  const label = normalizeQuantLabel(file);
  const total = file.shardTotal ?? 0;
  return `${label}::${total}`;
}

export function buildGgufArtifactGroups(files: HuggingFaceRepositoryFileDto[]): ArtifactGroup[] {
  const ggufs = files.filter((file) => file.category === 'gguf');
  const groups = new Map<string, HuggingFaceRepositoryFileDto[]>();

  for (const file of ggufs) {
    const key = file.sharded ? shardGroupKey(file) : `single::${file.path}`;
    const bucket = groups.get(key) ?? [];
    bucket.push(file);
    groups.set(key, bucket);
  }

  const result: ArtifactGroup[] = [];

  for (const [key, bucket] of groups.entries()) {
    const sorted = bucket.slice().sort((a, b) => {
      const ai = a.shardIndex ?? 0;
      const bi = b.shardIndex ?? 0;
      if (ai !== bi) {
        return ai - bi;
      }
      return a.path.localeCompare(b.path);
    });

    const sharded = sorted.some((file) => file.sharded);
    const shardTotal = sorted[0]?.shardTotal ?? (sharded ? sorted.length : 1);
    if (sharded) {
      const indexes = new Set(sorted.map((file) => file.shardIndex).filter((v) => v != null));
      if (indexes.size !== shardTotal || sorted.length !== shardTotal) {
        continue;
      }
    }

    const label = normalizeQuantLabel(sorted[0]);
    const totalBytes = sorted.reduce((sum, file) => sum + (file.size ?? 0), 0);
    result.push({
      id: key,
      label,
      files: sorted.map((file) => file.path),
      totalBytes,
      sharded,
      shardCount: shardTotal,
    });
  }

  return result.sort((a, b) => a.label.localeCompare(b.label));
}

export function buildMmprojCandidates(files: HuggingFaceRepositoryFileDto[]): string[] {
  return files
    .filter((file) => file.category === 'mmproj' && !file.sharded)
    .map((file) => file.path)
    .sort((a, b) => a.localeCompare(b));
}
