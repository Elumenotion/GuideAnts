import { useState } from 'react';
import { ConfirmationDialog } from '../../../components/common/ConfirmationDialog';
import { api } from '../../../services/api';
import type { LlamaInstallationDetailDto, ModelDownloadOperationDto } from '../../../types/settings';
import { getErrorMessage } from '../../../pages/settings/utils';
import { useLocalModelOnboardingOperation } from '../useOperationPolling';

export interface RepairInstallationDialogProps {
  isOpen: boolean;
  detail: LlamaInstallationDetailDto;
  onClose: () => void;
  onCompleted: () => Promise<void>;
}

export function RepairInstallationDialog({ isOpen, detail, onClose, onCompleted }: RepairInstallationDialogProps) {
  const [operationId, setOperationId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [operation, setOperation] = useState<ModelDownloadOperationDto | null>(null);

  useLocalModelOnboardingOperation({
    operationId,
    enabled: !!operationId,
    pollRoute: 'operations',
    onUpdate: setOperation,
    onTerminal: (next) => {
      if (next.status === 'completed') {
        void onCompleted();
      }
    },
  });

  const confirm = async () => {
    setError(null);
    try {
      const response = await api.settings.repairLlamaInstallation(detail.modelId, { confirm: true });
      setOperationId(response.operationId);
    } catch (submitError) {
      setError(getErrorMessage(submitError, 'Failed to start repair.'));
    }
  };

  return (
    <ConfirmationDialog
      isOpen={isOpen && !operationId}
      onClose={onClose}
      onConfirm={() => void confirm()}
      title="Repair installation"
      message={`Repair re-downloads and verifies artifacts from the recorded source for ${detail.modelId}.`}
      body={
        <div className="space-y-2 text-sm text-gray-700">
          <p>Repository: <span className="font-mono">{detail.repository ?? 'unknown'}</span></p>
          <p>Revision: <span className="font-mono break-all">{detail.resolvedRevision ?? 'unknown'}</span></p>
          {error ? <p className="text-red-700">{error}</p> : null}
          {operation ? <p>Repair operation {operation.status}</p> : null}
        </div>
      }
      confirmText="Start repair"
    />
  );
}
