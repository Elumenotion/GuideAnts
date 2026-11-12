import { FaTrash } from 'react-icons/fa';
import { api } from '../../../services/api';
import { textButtonClassName } from '../../../pages/settings/components/shared/ActionButtons';

/**
 * T-C4 (F-1): the selected artifact is not in the last-known inventory
 * (selectionExists === false) → offer Remove against a named condition, not a phantom
 * "(active)" marker. Remove succeeds against an unreachable engine (the server deletes
 * the install row and clears the selection; disk cleanup is best-effort). Re-download is
 * deliberately NOT offered here (R-B): it requires a live engine and fails with a named
 * ENDPOINT_UNREACHABLE reason there.
 */
export function RemovedArtifactNotice({
  serviceId,
  modelRef,
  onRemoved,
}: {
  serviceId: string;
  modelRef: string | null;
  onRemoved: () => Promise<void> | void;
}) {
  if (!modelRef) return null;

  const remove = async () => {
    await api.settings.localModels.remove(serviceId, modelRef);
    await onRemoved();
  };

  return (
    <button
      type="button"
      className={`${textButtonClassName('danger')} w-full justify-start`}
      onClick={() => {
        void remove();
      }}
    >
      <FaTrash className="w-3 h-3" />
      Remove '{modelRef}' from saved models
    </button>
  );
}
