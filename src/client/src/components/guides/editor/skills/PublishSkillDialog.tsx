import { useState } from 'react';
import { FaSpinner, FaStore } from 'react-icons/fa';
import { api } from '../../../../services/api';
import type { AssistantSkillDto, SkillPublishResultDto } from '../../../../types/guides';

interface PublishSkillDialogProps {
  isOpen: boolean;
  assistantId: string;
  skill: AssistantSkillDto;
  onClose: () => void;
  onPublished: (result: SkillPublishResultDto) => void;
}

/**
 * Publishes the assistant's saved skill package into the skill catalog. Publishing reads the
 * saved rows on the server, so an unsaved skill cannot be published - the dialog says so rather
 * than publishing stale bytes.
 */
export function PublishSkillDialog({
  isOpen,
  assistantId,
  skill,
  onClose,
  onPublished,
}: PublishSkillDialogProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!isOpen) {
    return null;
  }

  const isSaved = skill.files.length > 0 && !skill.files[0].id.startsWith('pending-');

  const publish = async () => {
    setBusy(true);
    setError(null);
    try {
      onPublished(await api.skillStore.publish(assistantId, skill.name));
      onClose();
    } catch (publishError) {
      setError(publishError instanceof Error ? publishError.message : 'Publish failed.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true">
      <div className="w-full max-w-lg rounded-lg bg-white p-6 shadow-xl">
        <div className="flex items-center gap-2">
          <FaStore className="h-4 w-4 text-blue-600" />
          <h2 className="text-lg font-semibold text-gray-900">Publish “{skill.name}” to the skill store</h2>
        </div>

        <p className="mt-2 text-sm text-gray-600">{skill.description}</p>

        <dl className="mt-4 grid grid-cols-2 gap-3 text-sm">
          <div className="rounded-md bg-gray-50 px-3 py-2">
            <dt className="text-xs text-gray-500">Package files</dt>
            <dd className="font-medium text-gray-900">{skill.files.length}</dd>
          </div>
          <div className="rounded-md bg-gray-50 px-3 py-2">
            <dt className="text-xs text-gray-500">Catalog entry</dt>
            <dd className="font-medium text-gray-900">
              {skill.catalogSkillId ? 'Existing entry, next version' : 'New entry at v1'}
            </dd>
          </div>
        </dl>

        {!isSaved && (
          <p className="mt-3 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
            This skill is not saved yet. Save the {assistantId ? 'assistant' : 'guide'} first — the
            store publishes the stored package, not unsaved edits.
          </p>
        )}

        {error && (
          <p className="mt-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
            {error}
          </p>
        )}

        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-gray-300 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={() => void publish()}
            disabled={busy || !isSaved || !assistantId}
            className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {busy && <FaSpinner className="h-3 w-3 animate-spin" />}
            Publish
          </button>
        </div>
      </div>
    </div>
  );
}
