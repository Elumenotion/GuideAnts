import type {
  HostMountListingDto,
  NotebookFileDto,
  NotebookFolderTreeDto,
} from '../types/notebook';
import type { FolderTreeDto, ProjectContentFile } from '../types/project';

/**
 * Merges a one-level host-mount listing into the folder tree at `listing.path`.
 * Existing child folders keep their nested children (lazy deeper state preserved).
 */
export function mergeHostMountListingIntoTree(
  tree: NotebookFolderTreeDto,
  listing: HostMountListingDto,
): NotebookFolderTreeDto {
  const targetPath = (listing.path || '').replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');

  const mergeAt = (node: NotebookFolderTreeDto): NotebookFolderTreeDto => {
    const nodePath = (node.relativePath || '').replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
    if (nodePath === targetPath) {
      const existingByPath = new Map(
        node.subFolders.map((f) => [f.relativePath.replace(/\\/g, '/'), f]),
      );

      const mergedFolders: NotebookFolderTreeDto[] = listing.folders.map((folder) => {
        const existing = existingByPath.get(folder.relativePath.replace(/\\/g, '/'));
        if (existing) {
          return existing;
        }
        return {
          name: folder.name,
          relativePath: folder.relativePath,
          subFolders: [],
          files: [],
        };
      });

      for (const [path, folder] of existingByPath) {
        if (!mergedFolders.some((f) => f.relativePath.replace(/\\/g, '/') === path)) {
          mergedFolders.push(folder);
        }
      }

      mergedFolders.sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));

      const files: NotebookFileDto[] = listing.files.map((file) => ({
        id: file.id,
        fileName: file.fileName,
        relativePath: file.relativePath,
        fileSize: file.fileSize,
        lastModifiedUtc: file.lastModifiedUtc,
        fileHash: file.fileHash,
        index: false,
        isIndexed: false,
        isLinked: file.isLinked ?? true,
      }));

      return {
        ...node,
        subFolders: mergedFolders,
        files,
      };
    }

    return {
      ...node,
      subFolders: node.subFolders.map(mergeAt),
    };
  };

  return mergeAt(tree);
}

/**
 * When a polled shallow tree arrives, graft previously lazy-loaded subtrees back
 * onto matching folder paths so poll does not wipe expand results.
 */
export function graftLazyMountBranches(
  polledTree: NotebookFolderTreeDto,
  previousTree: NotebookFolderTreeDto | null,
  listingCompletePaths: Set<string>,
): NotebookFolderTreeDto {
  if (!previousTree || listingCompletePaths.size === 0) {
    return polledTree;
  }

  const previousByPath = new Map<string, NotebookFolderTreeDto>();
  const index = (node: NotebookFolderTreeDto) => {
    const path = (node.relativePath || '').replace(/\\/g, '/');
    if (path) {
      previousByPath.set(path, node);
    }
    node.subFolders.forEach(index);
  };
  index(previousTree);

  const graft = (node: NotebookFolderTreeDto): NotebookFolderTreeDto => {
    const path = (node.relativePath || '').replace(/\\/g, '/');
    const prev = path ? previousByPath.get(path) : undefined;

    let subFolders = node.subFolders.map(graft);
    let files = node.files;

    if (path && listingCompletePaths.has(path) && prev) {
      const polledFolderPaths = new Set(subFolders.map((f) => f.relativePath.replace(/\\/g, '/')));
      for (const prevChild of prev.subFolders) {
        const childPath = prevChild.relativePath.replace(/\\/g, '/');
        if (!polledFolderPaths.has(childPath)) {
          subFolders = [...subFolders, graft(prevChild)];
        }
      }
      if (prev.files.length > 0 || listingCompletePaths.has(path)) {
        const byPath = new Map(files.map((f) => [f.relativePath.replace(/\\/g, '/'), f]));
        for (const prevFile of prev.files) {
          byPath.set(prevFile.relativePath.replace(/\\/g, '/'), prevFile);
        }
        files = Array.from(byPath.values());
      }
    }

    subFolders = subFolders
      .map((child) => {
        const childPath = child.relativePath.replace(/\\/g, '/');
        const prevChild = previousByPath.get(childPath);
        if (prevChild && listingCompletePaths.has(childPath)) {
          return graft({
            ...child,
            subFolders: prevChild.subFolders,
            files: prevChild.files.length ? prevChild.files : child.files,
          });
        }
        return child;
      })
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));

    return { ...node, subFolders, files };
  };

  return graft(polledTree);
}

function createProjectFolderId(relativePath: string): string {
  let hash = 0;
  for (let i = 0; i < relativePath.length; i++) {
    hash = ((hash << 5) - hash) + relativePath.charCodeAt(i);
    hash |= 0;
  }
  const hex = (hash >>> 0).toString(16).padStart(8, '0');
  return `00000000-0000-4000-8000-${hex.padStart(12, '0').slice(-12)}`;
}

export function mergeHostMountListingIntoProjectTree(
  tree: FolderTreeDto,
  listing: HostMountListingDto,
): FolderTreeDto {
  const targetPath = (listing.path || '').replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');

  const mergeAt = (node: FolderTreeDto): FolderTreeDto => {
    const nodePath = (node.relativePath || '').replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
    if (nodePath === targetPath) {
      const existingByPath = new Map(
        node.subFolders.map((f) => [f.relativePath.replace(/\\/g, '/'), f]),
      );

      const mergedFolders: FolderTreeDto[] = listing.folders.map((folder) => {
        const existing = existingByPath.get(folder.relativePath.replace(/\\/g, '/'));
        if (existing) {
          return existing;
        }
        return {
          id: createProjectFolderId(folder.relativePath),
          name: folder.name,
          relativePath: folder.relativePath,
          subFolders: [],
          files: [],
          isLinked: true,
        };
      });

      for (const [path, folder] of existingByPath) {
        if (!mergedFolders.some((f) => f.relativePath.replace(/\\/g, '/') === path)) {
          mergedFolders.push(folder);
        }
      }

      mergedFolders.sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));

      const files: ProjectContentFile[] = listing.files.map((file) => ({
        id: file.id,
        fileName: file.fileName,
        path: '',
        relativePath: file.relativePath,
        contentType: 'application/octet-stream',
        index: false,
        documentId: '',
        created: file.lastModifiedUtc,
        fileSize: file.fileSize,
        folderId: undefined,
        folderPath: targetPath,
        latestVersion: 0,
        isSnapshot: false,
        hasMarkdownShadow: false,
        markdownStatus: null,
        markdownProcessedAt: null,
      }));

      return {
        ...node,
        subFolders: mergedFolders,
        files,
      };
    }

    return {
      ...node,
      subFolders: node.subFolders.map(mergeAt),
    };
  };

  return mergeAt(tree);
}

export function graftLazyMountBranchesProject(
  polledTree: FolderTreeDto,
  previousTree: FolderTreeDto | null,
  listingCompletePaths: Set<string>,
): FolderTreeDto {
  if (!previousTree || listingCompletePaths.size === 0) {
    return polledTree;
  }

  const previousByPath = new Map<string, FolderTreeDto>();
  const index = (node: FolderTreeDto) => {
    const path = (node.relativePath || '').replace(/\\/g, '/');
    if (path) {
      previousByPath.set(path, node);
    }
    node.subFolders.forEach(index);
  };
  index(previousTree);

  const graft = (node: FolderTreeDto): FolderTreeDto => {
    const path = (node.relativePath || '').replace(/\\/g, '/');
    const prev = path ? previousByPath.get(path) : undefined;
    let subFolders = node.subFolders.map(graft);
    let files = node.files;

    if (path && listingCompletePaths.has(path) && prev) {
      const polledFolderPaths = new Set(subFolders.map((f) => f.relativePath.replace(/\\/g, '/')));
      for (const prevChild of prev.subFolders) {
        const childPath = prevChild.relativePath.replace(/\\/g, '/');
        if (!polledFolderPaths.has(childPath)) {
          subFolders = [...subFolders, graft(prevChild)];
        }
      }
      const byPath = new Map(files.map((f) => [f.relativePath.replace(/\\/g, '/'), f]));
      for (const prevFile of prev.files) {
        byPath.set(prevFile.relativePath.replace(/\\/g, '/'), prevFile);
      }
      files = Array.from(byPath.values());
    }

    subFolders = subFolders
      .map((child) => {
        const childPath = child.relativePath.replace(/\\/g, '/');
        const prevChild = previousByPath.get(childPath);
        if (prevChild && listingCompletePaths.has(childPath)) {
          return graft({
            ...child,
            subFolders: prevChild.subFolders,
            files: prevChild.files.length ? prevChild.files : child.files,
          });
        }
        return child;
      })
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));

    return { ...node, subFolders, files };
  };

  return graft(polledTree);
}
