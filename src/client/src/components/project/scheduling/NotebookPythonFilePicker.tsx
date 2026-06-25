import { useEffect, useMemo, useState } from 'react';
import { api } from '../../../services/api';
import type { NotebookFileDto } from '../../../types/notebook';

interface NotebookPythonFilePickerProps {
  projectId: string;
  notebookId: string;
  selectedFileId?: string | null;
  onSelect: (fileId: string, relativePath: string) => void;
  disabled?: boolean;
}

function flattenPythonFiles(files: NotebookFileDto[]): NotebookFileDto[] {
  return files.filter((file) => file.relativePath.toLowerCase().endsWith('.py'));
}

export function NotebookPythonFilePicker({
  projectId,
  notebookId,
  selectedFileId,
  onSelect,
  disabled = false,
}: NotebookPythonFilePickerProps) {
  const [files, setFiles] = useState<NotebookFileDto[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    const load = async () => {
      if (!projectId || !notebookId) {
        setFiles([]);
        return;
      }

      setIsLoading(true);
      setError(null);
      try {
        const allFiles = await api.projects.notebooks.getNotebookFiles(projectId, notebookId);
        if (!cancelled) {
          setFiles(flattenPythonFiles(allFiles));
        }
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Failed to load notebook files');
          setFiles([]);
        }
      } finally {
        if (!cancelled) {
          setIsLoading(false);
        }
      }
    };

    load();
    return () => {
      cancelled = true;
    };
  }, [projectId, notebookId]);

  const selectedPath = useMemo(
    () => files.find((file) => file.id === selectedFileId)?.relativePath ?? '',
    [files, selectedFileId],
  );

  return (
    <div>
      <label htmlFor="python-script-file" className="block text-sm font-medium text-gray-700 mb-1">
        Python script (.py)
      </label>
      <select
        id="python-script-file"
        value={selectedFileId ?? ''}
        onChange={(e) => {
          const file = files.find((f) => f.id === e.target.value);
          if (file) {
            onSelect(file.id, file.relativePath);
          }
        }}
        disabled={disabled || isLoading || files.length === 0}
        className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
        aria-describedby="python-script-help"
      >
        <option value="">
          {isLoading ? 'Loading scripts…' : 'Select a Python file'}
        </option>
        {files.map((file) => (
          <option key={file.id} value={file.id}>
            {file.relativePath}
          </option>
        ))}
      </select>
      <p id="python-script-help" className="mt-1 text-xs text-gray-500">
        {error
          ? error
          : files.length === 0 && !isLoading
            ? 'No .py files found in this notebook.'
            : selectedPath
              ? `Selected: ${selectedPath}`
              : 'Scripts are executed via the RunPython sandbox path.'}
      </p>
    </div>
  );
}
