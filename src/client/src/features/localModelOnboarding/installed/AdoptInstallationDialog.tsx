import { useEffect, useState } from 'react';
import { ConfirmationDialog } from '../../../components/common/ConfirmationDialog';
import { api } from '../../../services/api';
import type { AdoptPreviewResponseDto, LlamaInstallationDetailDto } from '../../../types/settings';
import { getErrorMessage } from '../../../pages/settings/utils';

export interface AdoptInstallationDialogProps {
  isOpen: boolean;
  detail: LlamaInstallationDetailDto;
  onClose: () => void;
  onCompleted: () => Promise<void>;
}

export function AdoptInstallationDialog({ isOpen, detail, onClose, onCompleted }: AdoptInstallationDialogProps) {
  const [catalogId, setCatalogId] = useState(detail.catalogId ?? '');
  const [catalogVersion, setCatalogVersion] = useState(detail.catalogVersion ?? '');
  const [preview, setPreview] = useState<AdoptPreviewResponseDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!isOpen) {
      setPreview(null);
      setError(null);
    }
  }, [isOpen]);

  const loadPreview = async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await api.settings.adoptLlamaInstallation(detail.modelId, {
        catalogId: catalogId.trim(),
        catalogVersion: catalogVersion.trim(),
        confirm: false,
      });
      setPreview(response as AdoptPreviewResponseDto);
    } catch (previewError) {
      setError(getErrorMessage(previewError, 'Failed to load adoption preview.'));
    } finally {
      setLoading(false);
    }
  };

  const confirm = async () => {
    setLoading(true);
    setError(null);
    try {
      await api.settings.adoptLlamaInstallation(detail.modelId, {
        catalogId: catalogId.trim(),
        catalogVersion: catalogVersion.trim(),
        confirm: true,
      });
      await onCompleted();
    } catch (submitError) {
      setError(getErrorMessage(submitError, 'Adoption failed.'));
    } finally {
      setLoading(false);
    }
  };

  return (
    <ConfirmationDialog
      isOpen={isOpen}
      onClose={onClose}
      onConfirm={() => void (preview?.canAdopt ? confirm() : loadPreview())}
      isLoading={loading}
      confirmDisabled={!catalogId.trim() || !catalogVersion.trim()}
      title="Adopt curated tracking"
      message="Review every difference before confirming. Unknown values are never filled automatically."
      body={
        <div className="space-y-3 text-sm text-gray-700">
          <div className="grid grid-cols-1 gap-2 md:grid-cols-2">
            <input
              type="text"
              value={catalogId}
              onChange={(event) => setCatalogId(event.target.value)}
              placeholder="catalogId"
              className="rounded border border-gray-300 px-2 py-1.5 font-mono text-sm"
            />
            <input
              type="text"
              value={catalogVersion}
              onChange={(event) => setCatalogVersion(event.target.value)}
              placeholder="catalogVersion"
              className="rounded border border-gray-300 px-2 py-1.5 font-mono text-sm"
            />
          </div>
          {preview ? (
            <div className="overflow-x-auto rounded border border-gray-200">
              <table className="min-w-full text-xs">
                <thead>
                  <tr className="border-b border-gray-200 bg-gray-50 text-left">
                    <th className="px-2 py-1">Field</th>
                    <th className="px-2 py-1">Current</th>
                    <th className="px-2 py-1">Curated</th>
                    <th className="px-2 py-1">Action</th>
                  </tr>
                </thead>
                <tbody>
                  {preview.differences.map((diff) => (
                    <tr key={diff.field} className="border-b border-gray-100">
                      <td className="px-2 py-1 font-mono">{diff.field}</td>
                      <td className="px-2 py-1 font-mono">{diff.currentValue ?? '—'}</td>
                      <td className="px-2 py-1 font-mono">{diff.curatedValue ?? '—'}</td>
                      <td className="px-2 py-1">{diff.requiredAction ?? (diff.verifiable ? 'verifiable' : 'unknown')}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {preview.blockers.length > 0 ? (
                <div className="border-t border-amber-200 bg-amber-50 px-2 py-2 text-amber-900">
                  Blockers: {preview.blockers.join('; ')}
                </div>
              ) : null}
            </div>
          ) : null}
          {error ? <p className="text-red-700">{error}</p> : null}
        </div>
      }
      confirmText={preview?.canAdopt ? 'Confirm adoption' : 'Preview differences'}
    />
  );
}
