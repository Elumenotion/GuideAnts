import { useMemo, useState } from 'react';
import {
  FaChevronDown,
  FaChevronRight,
  FaCode,
  FaDownload,
  FaEye,
  FaFile,
  FaFolder,
} from 'react-icons/fa';
import type { FileDto } from '../../../../types/guides';
import {
  buildSkillFileTree,
  isSkillFilePreviewable,
  type SkillFileTreeNode,
} from './skillFileTreeModel';

interface SkillFileTreeProps {
  skillName: string;
  files: FileDto[];
  onPreviewFile: (file: FileDto) => void;
  onDownloadFile?: (fileId: string, fileName: string) => void;
}

function TreeNode({
  node,
  depth,
  onPreviewFile,
  onDownloadFile,
}: {
  node: SkillFileTreeNode;
  depth: number;
  onPreviewFile: (file: FileDto) => void;
  onDownloadFile?: (fileId: string, fileName: string) => void;
}) {
  const [expanded, setExpanded] = useState(depth < 2);

  if (node.isFolder) {
    return (
      <div>
        <button
          type="button"
          onClick={() => setExpanded((value) => !value)}
          className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm text-gray-800 hover:bg-blue-50"
          style={{ paddingLeft: `${depth * 16 + 8}px` }}
        >
          {expanded ? (
            <FaChevronDown className="h-3 w-3 text-gray-500" />
          ) : (
            <FaChevronRight className="h-3 w-3 text-gray-500" />
          )}
          <FaFolder className="h-4 w-4 text-blue-600" />
          <span className="font-medium">{node.name}</span>
        </button>
        {expanded && (
          <div>
            {node.children.map((child) => (
              <TreeNode
                key={`${child.relativePath}-${child.name}`}
                node={child}
                depth={depth + 1}
                onPreviewFile={onPreviewFile}
                onDownloadFile={onDownloadFile}
              />
            ))}
          </div>
        )}
      </div>
    );
  }

  const file = node.file;
  if (!file) {
    return null;
  }

  const previewable = isSkillFilePreviewable(file.relativePath);
  const fileName = node.name;
  const isScript = file.relativePath.includes('/scripts/');

  return (
    <div
      className="flex items-center justify-between rounded-md border border-blue-100 bg-white px-3 py-2"
      style={{ marginLeft: `${depth * 16 + 8}px` }}
    >
      <div className="flex min-w-0 items-center gap-2">
        {isScript ? (
          <FaCode className="h-4 w-4 shrink-0 text-blue-600" />
        ) : (
          <FaFile className="h-4 w-4 shrink-0 text-blue-600" />
        )}
        <span className="truncate text-sm font-medium text-gray-900">{fileName}</span>
        {file.id.startsWith('pending-') && (
          <span className="text-xs text-orange-600">(pending save)</span>
        )}
      </div>
      <div className="flex items-center gap-1">
        {previewable && (
          <button
            type="button"
            onClick={() => onPreviewFile(file)}
            className="rounded p-1 text-blue-600 hover:bg-blue-50"
            title="Preview file"
          >
            <FaEye className="h-4 w-4" />
          </button>
        )}
        {onDownloadFile && !file.id.startsWith('pending-') && (
          <button
            type="button"
            onClick={() => onDownloadFile(file.id, fileName)}
            className="rounded p-1 text-blue-600 hover:bg-blue-50"
            title="Download file"
          >
            <FaDownload className="h-4 w-4" />
          </button>
        )}
      </div>
    </div>
  );
}

export function SkillFileTree({
  skillName,
  files,
  onPreviewFile,
  onDownloadFile,
}: SkillFileTreeProps) {
  const tree = useMemo(() => buildSkillFileTree(files, skillName), [files, skillName]);

  if (tree.length === 0) {
    return (
      <p className="text-sm text-gray-500">No files in this skill package yet.</p>
    );
  }

  return (
    <div className="space-y-1 rounded-md border border-blue-100 bg-blue-50/40 p-2">
      {tree.map((node) => (
        <TreeNode
          key={`${node.relativePath}-${node.name}`}
          node={node}
          depth={0}
          onPreviewFile={onPreviewFile}
          onDownloadFile={onDownloadFile}
        />
      ))}
    </div>
  );
}
