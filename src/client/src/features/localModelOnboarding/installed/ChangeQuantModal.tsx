import { useEffect, useState } from 'react';
import { SettingsModal } from '../../../pages/settings/components/shared/SettingsModal';
import { TextActionButton } from '../../../pages/settings/components/shared/ActionButtons';
import { api } from '../../../services/api';
import type { LlamaCatalogQuantsResponseDto, LlamaInstallationDetailDto } from '../../../types/settings';
import type { ActiveModelOperationState } from '../../../pages/settings/types';
import { getErrorMessage, parseProblemDetails, type ApiProblemDetails } from '../../../pages/settings/utils';
import { QuantSelect, formatQuantSummary } from '../curated/QuantSelect';

export interface ChangeQuantModalProps {
  isOpen: boolean;
  detail: LlamaInstallationDetailDto;
  onClose: () => void;
  /**
   * Hands the started download to the settings page, which owns progress,
   * completion toasts, and catalog refresh for long-running model operations.
   */
  onOperationStarted: (operation: ActiveModelOperationState) => void;
}

export function ChangeQuantModal({ isOpen, detail, onClose, onOperationStarted }: ChangeQuantModalProps) {
  const [quants, setQuants] = useState<LlamaCatalogQuantsResponseDto | null>(null);
  const [selectedQuantId, setSelectedQuantId] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<ApiProblemDetails | null>(null);
  const [submitting, setSubmitting] = useState(false);

  // Clear per-attempt state on open so a previous failure cannot leak into the next attempt.
  useEffect(() => {
    if (!isOpen) {
      return;
    }
    setSelectedQuantId('');
    setError(null);
    setSubmitting(false);
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen || !detail.catalogId) {
      return;
    }
    const catalogId = detail.catalogId;
    const catalogVersion = detail.catalogVersion ?? undefined;
    let cancelled = false;
    setLoading(true);
    void (async () => {
      try {
        const response = await api.settings.getLlamaCatalogQuants(catalogId, catalogVersion);
        if (!cancelled) {
          setQuants(response);
          setError(null);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(
            parseProblemDetails(loadError) ?? {
              title: 'Failed to load quant groups',
              detail: getErrorMessage(loadError, 'Failed to load quant groups.'),
            },
          );
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

  const installedQuant = quants?.quants.find((quant) => quant.id === detail.quantId) ?? null;
  const installedName = installedQuant?.label ?? detail.quantLabel ?? detail.quantId;
  const installedSummary = installedQuant ? formatQuantSummary(installedQuant) : null;

  const submit = async () => {
    if (!quants || !selectedQuantId) {
      setError({ detail: 'Select a quant group.' });
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      const response = await api.settings.changeLlamaInstallationQuant(detail.modelId, {
        quantId: selectedQuantId,
        resolvedRevision: quants.resolvedRevision,
      });
      onOperationStarted({
        operationId: response.operationId,
        kind: 'changeQuant',
        pollRoute: 'operations',
        routerModelId: detail.routerModelId,
        catalogModelId: detail.modelId,
      });
      onClose();
    } catch (submitError) {
      setError(
        parseProblemDetails(submitError) ?? {
          title: 'Change quant failed',
          detail: getErrorMessage(submitError, 'Failed to start change quant.'),
        },
      );
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <SettingsModal
      isOpen={isOpen}
      title={`Change quant: ${detail.modelId}`}
      onClose={onClose}
      disableDismiss={submitting}
      footer={
        <>
          <TextActionButton tone="neutral" onClick={onClose} disabled={submitting}>
            Cancel
          </TextActionButton>
          <TextActionButton
            tone="primary"
            onClick={() => void submit()}
            disabled={submitting || !selectedQuantId}
          >
            Start change quant
          </TextActionButton>
        </>
      }
    >
      {loading ? <p className="text-sm text-gray-600">Loading quant groups…</p> : null}
      {error ? (
        <div role="alert" className="mb-3 rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          {error.title ? <div className="font-medium">{error.title}</div> : null}
          {error.detail ? <div className={error.title ? 'mt-0.5' : undefined}>{error.detail}</div> : null}
          {error.remediation ? <div className="mt-1 text-xs text-red-600">{error.remediation}</div> : null}
          {error.code ? <div className="mt-1 font-mono text-[11px] text-red-500">{error.code}</div> : null}
        </div>
      ) : null}
      {quants ? (
        <div className="space-y-3">
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs text-gray-600">
            <dt className="text-gray-500">Repository</dt>
            <dd className="font-mono break-all text-gray-800">
              {quants.repository} @ {quants.resolvedRevision}
            </dd>
            <dt className="text-gray-500">Installed quant</dt>
            <dd className="text-gray-800">
              {installedName ? (
                <>
                  <span className="font-mono font-medium text-gray-900">{installedName}</span>
                  {installedSummary ? <span className="text-gray-500"> · {installedSummary}</span> : null}
                </>
              ) : (
                <span className="text-gray-500">Not recorded for this installation</span>
              )}
            </dd>
          </dl>
          <QuantSelect
            selectId="change-quant-select"
            label="New quant group"
            placeholder="Select a different quant…"
            quants={quants.quants}
            selectedQuantId={selectedQuantId || null}
            onSelect={setSelectedQuantId}
            disabled={submitting}
            installedQuantId={detail.quantId}
            installedQuantLabel={detail.quantLabel}
          />
          <p className="text-xs text-gray-500">
            Downloads only what is missing. Other quants already on disk for this model stay there
            so you can switch back without downloading again. Progress appears on this model&apos;s
            row in the model list.
          </p>
        </div>
      ) : null}
    </SettingsModal>
  );
}
