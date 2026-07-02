import type { FileDto } from '../../../../types/guides';

export interface SkillFileTreeNode {
  name: string;
  relativePath: string;
  file?: FileDto;
  children: SkillFileTreeNode[];
  isFolder: boolean;
}

const PREVIEWABLE_EXTENSIONS = new Set([
  'md',
  'markdown',
  'txt',
  'py',
  'sh',
  'json',
  'yaml',
  'yml',
  'tmpl',
  'bib',
  'tex',
]);

export function skillPackagePath(relativePath: string, skillName: string): string {
  const normalized = relativePath.replace(/\\/g, '/');
  const prefix = `Skills/${skillName}/`;
  if (normalized.startsWith(prefix)) {
    return normalized.slice(prefix.length);
  }

  const match = normalized.match(/^Skills\/[^/]+\/(.+)$/i);
  return match?.[1] ?? normalized;
}

export function buildSkillFileTree(files: FileDto[], skillName: string): SkillFileTreeNode[] {
  const root: SkillFileTreeNode[] = [];

  for (const file of files) {
    const packagePath = skillPackagePath(file.relativePath, skillName);
    const segments = packagePath.split('/').filter(Boolean);
    let level = root;
    let pathSoFar = '';

    for (let index = 0; index < segments.length; index += 1) {
      const segment = segments[index];
      const isLast = index === segments.length - 1;
      pathSoFar = pathSoFar ? `${pathSoFar}/${segment}` : segment;

      if (isLast) {
        level.push({
          name: segment,
          relativePath: file.relativePath,
          file,
          children: [],
          isFolder: false,
        });
        continue;
      }

      let folder = level.find((node) => node.isFolder && node.name === segment);
      if (!folder) {
        folder = {
          name: segment,
          relativePath: `Skills/${skillName}/${pathSoFar}`,
          children: [],
          isFolder: true,
        };
        level.push(folder);
      }

      level = folder.children;
    }
  }

  return sortSkillFileTreeNodes(root);
}

function sortSkillFileTreeNodes(nodes: SkillFileTreeNode[]): SkillFileTreeNode[] {
  return nodes
    .map((node) => ({
      ...node,
      children: sortSkillFileTreeNodes(node.children),
    }))
    .sort((left, right) => {
      if (left.isFolder !== right.isFolder) {
        return left.isFolder ? -1 : 1;
      }

      if (left.name === 'SKILL.md') {
        return -1;
      }

      if (right.name === 'SKILL.md') {
        return 1;
      }

      return left.name.localeCompare(right.name, undefined, { sensitivity: 'base' });
    });
}

export function isSkillFilePreviewable(relativePath: string): boolean {
  const extension = relativePath.split('.').pop()?.toLowerCase() ?? '';
  return PREVIEWABLE_EXTENSIONS.has(extension);
}

export function decodePendingFileContent(contentBytes: string): string {
  return decodeURIComponent(escape(atob(contentBytes)));
}
