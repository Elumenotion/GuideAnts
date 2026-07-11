import { useEffect, useState } from 'react';
import { SettingsModal } from '../../../pages/settings/components/shared/SettingsModal';
import { TextActionButton } from '../../../pages/settings/components/shared/ActionButtons';
import { api } from '../../../services/api';
import type { LlamaCatalogQuantsResponseDto, LlamaInstallationDetailDto, ModelDownloadOperationDto } from '../../../types/settings';
import { getErrorMessage } from '../../../pages/settings/utils';
import { formatBytes } from '../curated/format';
import { useLocalModelOnboardingOperation } from '../useOperationPolling';

export interface ChangeQuantModalProps {
  isOpen: boolean;
  detail: LlamaInstallationDetailDto;
  onClose: () => void;
  onCompleted: () => Promise<void>;
}

export function ChangeQuantModal({ isOpen, detail, onClose, onCompleted }: ChangeQuantModalProps) {
  const [quants, setQuants] = useState<LlamaCatalogQuantsResponseDto | null>(null);
  const [selectedQuantId, setSelectedQuantId] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [operationId, setOperationId] = useState<string | null>(null);
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

  useEffect(() => {
    if (!isOpen || !detail.catalogId || !detail.catalogVersion) {
      return;
    }
    let cancelled = false;
    setLoading(true);
    void (async () => {
      try {
        const response = await api.settings.getLlamaCatalogQuants(detail.catalogId!, detail.catalogVersion!);
        if (!cancelled) {
          setQuants(response);
          setError(null);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(getErrorMessage(loadError, 'Failed to load quant groups.'));
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [isOpen, detail.catalogId, detail.catalogVersion]);

  const submit = async () => {
    if (!quants || !selectedQuantId) {
      setError('Select a quant group.');
      return;
    }
    setError(null);
    try {
      const response = await api.settings.changeLlamaInstallationQuant(detail.modelId, {
        quantId: selectedQuantId,
        resolvedRevision: quants.resolvedRevision,
      });
      setOperationId(response.operationId);
    } catch (submitError) {
      setError(getErrorMessage(submitError, 'Failed to start change quant.'));
    }
  };

  return (
    <SettingsModal
      isOpen={isOpen}
      title={`Change quant: ${detail.modelId}`}
      onClose={onClose}
      disableDismiss={!!operationId}
      footer={
        <>
          <TextActionButton tone="neutral" onClick={onClose} disabled={!!operationId}>
            Cancel
          </TextActionButton>
          <TextActionButton tone="primary" onClick={() => void submit()} disabled={!!operationId || !selectedQuantId}>
            Start change quant
          </TextActionButton>
        </>
      }
    >
      {loading ? <p className="text-sm text-gray-600">Loading quant groups…</p> : null}
      {error ? <div className="mb-3 rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div> : null}
      {quants ? (
        <div className="space-y-2">
          <p className="text-xs text-gray-600">Repository {quants.repository} @ {quants.resolvedRevision}</p>
          {quants.quants.map((quant) => (
            <label key={quant.id} className="flex cursor-pointer items-start gap-2 rounded border border-gray-200 px-3 py-2">
              <input
                type="radio"
                name="change-quant"
                checked={selectedQuantId === quant.id}
                onChange={() => setSelectedQuantId(quant.id)}
              />
              <span>
                <span className="font-medium text-gray-900">{quant.label}</span>
                <span className="ml-2 text-xs text-gray-500">{formatBytes(quant.totalBytes)} · {quant.files.length} file(s)</span>
              </span>
            </label>
          ))}
        </div>
      ) : null}
      {operation ? (
        <p className="mt-3 text-sm text-gray-700">Operation {operation.status}</p>
      ) : null}
    </SettingsModal>
  );
}
