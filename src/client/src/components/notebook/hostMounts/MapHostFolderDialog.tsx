import React, { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import type { HostFolderMountScope } from '../../../types/hostFolderMount';
import { canPickHostFolder, pickHostFolder } from '../../../utils/pickHostFolder';

interface MapHostFolderDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (values: { hostPath: string; scope: HostFolderMountScope; leafName: string }) => Promise<void>;
}

export function MapHostFolderDialog({ isOpen, onClose, onSubmit }: MapHostFolderDialogProps) {
  const [hostPath, setHostPath] = useState('');
  const [scope, setScope] = useState<HostFolderMountScope>('Notebook');
  const [leafName, setLeafName] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [isPickingFolder, setIsPickingFolder] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const folderPickerAvailable = canPickHostFolder();

  useEffect(() => {
    if (isOpen) {
      setHostPath('');
      setScope('Notebook');
      setLeafName('');
      setError(null);
    }
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !isSubmitting && !isPickingFolder) {
        onClose();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, isSubmitting, isPickingFolder, onClose]);

  const handleBrowse = async () => {
    try {
      setIsPickingFolder(true);
      setError(null);
      const result = await pickHostFolder();
      if (result.ok) {
        setHostPath(result.path);
      } else if (result.reason === 'no-path') {
        setError('Could not determine the full folder path from the selection. Enter the absolute path manually (e.g. D:\\Data\\Shared).');
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to open folder picker';
      setError(message);
    } finally {
      setIsPickingFolder(false);
    }
  };

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!hostPath.trim()) {
      setError('Host path is required.');
      return;
    }

    try {
      setIsSubmitting(true);
      setError(null);
      await onSubmit({
        hostPath: hostPath.trim(),
        scope,
        leafName: leafName.trim(),
      });
      onClose();
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to create host folder mount';
      setError(message);
    } finally {
      setIsSubmitting(false);
    }
  };

  if (!isOpen) {
    return null;
  }

  const dialogMarkup = (
    <div className="fixed inset-0 z-[9999] flex items-start justify-center bg-black bg-opacity-50">
      <div className="mx-4 mt-24 w-full max-w-lg rounded-lg bg-white p-6 shadow-xl">
        <h2 className="mb-4 text-lg font-bold text-gray-900">Map host folder here</h2>
        <form onSubmit={(event) => void handleSubmit(event)} className="space-y-4">
          <div>
            <label className="mb-1 block text-sm font-medium text-gray-700" htmlFor="host-mount-path">
              Host path
            </label>
            <div className="flex gap-2">
              <input
                id="host-mount-path"
                type="text"
                value={hostPath}
                onChange={(event) => setHostPath(event.target.value)}
                disabled={isSubmitting || isPickingFolder}
                className="min-w-0 flex-1 rounded border px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                placeholder="D:\Data\Shared or /home/user/shared"
                data-testid="host-mount-path-input"
              />
              {folderPickerAvailable && (
                <button
                  type="button"
                  onClick={() => void handleBrowse()}
                  disabled={isSubmitting || isPickingFolder}
                  className="shrink-0 rounded border border-gray-300 px-3 py-2 text-sm hover:bg-gray-50 disabled:opacity-50"
                  data-testid="host-mount-browse-button"
                >
                  {isPickingFolder ? 'Opening…' : 'Browse…'}
                </button>
              )}
            </div>
            {!folderPickerAvailable && (
              <p className="mt-1 text-xs text-gray-500">
                Folder picker is not available in this browser. Enter the full absolute path on the Docker host.
              </p>
            )}
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium text-gray-700" htmlFor="host-mount-scope">
              Scope
            </label>
            <select
              id="host-mount-scope"
              value={scope}
              onChange={(event) => setScope(event.target.value as HostFolderMountScope)}
              disabled={isSubmitting || isPickingFolder}
              className="w-full rounded border px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              data-testid="host-mount-scope-select"
            >
              <option value="Notebook">This notebook only</option>
              <option value="Project">All notebooks in project</option>
            </select>
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium text-gray-700" htmlFor="host-mount-leaf">
              Leaf name (optional)
            </label>
            <input
              id="host-mount-leaf"
              type="text"
              value={leafName}
              onChange={(event) => setLeafName(event.target.value)}
              disabled={isSubmitting || isPickingFolder}
              className="w-full rounded border px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="Defaults from host path"
              data-testid="host-mount-leaf-input"
            />
          </div>

          {error && (
            <p className="text-sm text-red-600" data-testid="host-mount-create-error">{error}</p>
          )}

          <div className="flex justify-end gap-2 pt-2">
            <button
              type="button"
              onClick={onClose}
              disabled={isSubmitting || isPickingFolder}
              className="rounded border border-gray-300 px-4 py-2 text-sm hover:bg-gray-50 disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={isSubmitting || isPickingFolder}
              className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
              data-testid="host-mount-create-submit"
            >
              {isSubmitting ? 'Creating…' : 'Create mapping'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );

  return createPortal(dialogMarkup, document.body);
}
