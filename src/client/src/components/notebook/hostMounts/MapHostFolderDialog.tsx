import React, { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import type { HostFolderMountScope } from '../../../types/hostFolderMount';

interface MapHostFolderDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (values: { hostPath: string; scope: HostFolderMountScope; leafName: string }) => Promise<void>;
  scope?: HostFolderMountScope;
}

export function MapHostFolderDialog({ isOpen, onClose, onSubmit, scope = 'Notebook' }: MapHostFolderDialogProps) {
  const [hostPath, setHostPath] = useState('');
  const [leafName, setLeafName] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (isOpen) {
      setHostPath('');
      setLeafName('');
      setError(null);
    }
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !isSubmitting) {
        onClose();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, isSubmitting, onClose]);

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
        <h2 className="mb-4 text-lg font-bold text-gray-900">
          {scope === 'Project' ? 'Map host folder to project' : 'Map host folder here'}
        </h2>
        <form onSubmit={(event) => void handleSubmit(event)} className="space-y-4">
          <div>
            <label className="mb-1 block text-sm font-medium text-gray-700" htmlFor="host-mount-path">
              Host path
            </label>
            <input
              id="host-mount-path"
              type="text"
              value={hostPath}
              onChange={(event) => setHostPath(event.target.value)}
              disabled={isSubmitting}
              className="w-full rounded border px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="D:\Data\Shared or /home/user/shared"
              data-testid="host-mount-path-input"
            />
            <p className="mt-1 text-xs text-gray-500">
              {scope === 'Project'
                ? 'Enter the full absolute path on the Docker host. This folder will appear at the project root and inside every notebook.'
                : 'Enter the full absolute path on the Docker host.'}
            </p>
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
              disabled={isSubmitting}
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
              disabled={isSubmitting}
              className="rounded border border-gray-300 px-4 py-2 text-sm hover:bg-gray-50 disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={isSubmitting}
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
