import { useEffect, useState } from 'react';
import { FaDownload, FaSpinner } from 'react-icons/fa';
import { api } from '../../../../services/api';
import type { LocalModelCatalogEntryDto } from '../../../../types/settings';
import { TextActionButton } from '../../components/shared/ActionButtons';
import { SettingsModal } from '../../components/shared/SettingsModal';
import {
  formatLocalModelCatalogLabel,
  parseLocalModelCatalog,
  sortLocalModelCatalogEntries,
  type LocalModelCatalogServiceId,
} from './localModelCatalog';

export type CatalogDownloadModelDialogProps = {
  serviceId: LocalModelCatalogServiceId;
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (values: { modelId: string; revision: string }) => Promise<void>;
  title: string;
  description: string;
  submitLabel: string;
  submitTitle?: string;
};

export function CatalogDownloadModelDialog({
  serviceId,
  isOpen,
  onClose,
  onSubmit,
  title,
  description,
  submitLabel,
  submitTitle,
}: CatalogDownloadModelDialogProps) {
  const [catalogEntries, setCatalogEntries] = useState<LocalModelCatalogEntryDto[]>([]);
  const [catalogLoading, setCatalogLoading] = useState(false);
  const [catalogError, setCatalogError] = useState<string | null>(null);
  const [modelId, setModelId] = useState('');
  const [revision, setRevision] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    let cancelled = false;
    setRevision('');
    setErr(null);
    setSubmitting(false);
    setCatalogLoading(true);
    setCatalogError(null);
    setCatalogEntries([]);
    setModelId('');

    void api.settings.localModels.catalogOutcome(serviceId).then((outcome) => {
      if (cancelled) {
        return;
      }
      setCatalogLoading(false);
      if (outcome.kind === 'error') {
        setCatalogError(outcome.message);
        return;
      }
      const entries = sortLocalModelCatalogEntries(parseLocalModelCatalog(outcome.payload));
      if (entries.length === 0) {
        setCatalogError('The curated catalog returned no models.');
        return;
      }
      setCatalogEntries(entries);
      setModelId(entries[0].id);
    });

    return () => {
      cancelled = true;
    };
  }, [isOpen, serviceId]);

  const submit = async () => {
    if (!modelId.trim()) {
      setErr('Select a catalog model.');
      return;
    }
    setSubmitting(true);
    setErr(null);
    try {
      await onSubmit({ modelId: modelId.trim(), revision });
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Download failed to start.');
    } finally {
      setSubmitting(false);
    }
  };

  const disableSubmit = submitting || catalogLoading || Boolean(catalogError) || !modelId.trim();

  return (
    <SettingsModal
      isOpen={isOpen}
      title={title}
      onClose={onClose}
      disableDismiss={submitting}
      footer={
        <>
          <TextActionButton tone="neutral" disabled={submitting} onClick={onClose}>
            Cancel
          </TextActionButton>
          <TextActionButton
            tone="primary"
            icon={submitting ? <FaSpinner className="animate-spin" /> : <FaDownload />}
            disabled={disableSubmit}
            onClick={() => void submit()}
            title={submitTitle}
          >
            {submitLabel}
          </TextActionButton>
        </>
      }
    >
      <div className="space-y-3 text-sm">
        <p className="text-xs text-gray-600">{description}</p>

        {catalogLoading ? (
          <p className="flex items-center gap-2 text-xs text-gray-600">
            <FaSpinner className="animate-spin" />
            Loading curated catalog…
          </p>
        ) : null}

        {catalogError ? <p className="text-xs text-red-700">{catalogError}</p> : null}

        {!catalogLoading && !catalogError ? (
          <>
            <label className="block">
              <span className="mb-1 block text-xs font-medium text-gray-700">Catalog model</span>
              <select
                className="w-full rounded border border-gray-300 px-2 py-1.5 text-sm"
                value={modelId}
                onChange={(e) => setModelId(e.target.value)}
                disabled={submitting}
              >
                {catalogEntries.map((entry) => (
                  <option key={entry.id} value={entry.id}>
                    {formatLocalModelCatalogLabel(entry)}
                  </option>
                ))}
              </select>
            </label>

            <label className="block">
              <span className="mb-1 block text-xs font-medium text-gray-700">Revision (optional)</span>
              <input
                type="text"
                value={revision}
                onChange={(e) => setRevision(e.target.value)}
                disabled={submitting}
                className="w-full rounded border border-gray-300 px-2 py-1.5 text-sm disabled:bg-gray-100"
                placeholder="main"
              />
              <span className="mt-1 block text-[11px] text-gray-500">
                Hugging Face revision / branch. Blank uses the catalog default.
              </span>
            </label>
          </>
        ) : null}

        {err ? <p className="text-xs text-red-700">{err}</p> : null}
      </div>
    </SettingsModal>
  );
}
