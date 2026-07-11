import { useCallback, useEffect, useState } from 'react';
import { api } from '../../../services/api';
import type { LlamaInstallationDetailDto, LlamaRouterEntryDto } from '../../../types/settings';
import { getErrorMessage } from '../../../pages/settings/utils';
import { formatBytes } from '../curated/format';
import { ChangeQuantModal } from './ChangeQuantModal';
import { AliasPresetSavePanel } from './AliasPresetSavePanel';
import { ModelChatBehaviorPanel } from './ModelChatBehaviorPanel';
import { TextActionButton } from '../../../pages/settings/components/shared/ActionButtons';

export interface LlamaInstalledSummaryProps {
  modelId: string;
  sharedProfileModelCount?: number;
  onChanged?: () => Promise<void>;
}

export function LlamaInstalledSummary({ modelId, sharedProfileModelCount, onChanged }: LlamaInstalledSummaryProps) {
  const [detail, setDetail] = useState<LlamaInstallationDetailDto | null>(null);
  const [routerEntry, setRouterEntry] = useState<LlamaRouterEntryDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [changeQuantOpen, setChangeQuantOpen] = useState(false);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [installation, entries] = await Promise.all([
        api.settings.getLlamaInstallationDetail(modelId),
        api.settings.getLlamaRouterEntries(),
      ]);
      setDetail(installation);
      setRouterEntry(entries.entries.find((entry) => entry.alias === installation.routerModelId) ?? null);
    } catch (loadError) {
      setError(getErrorMessage(loadError, 'Failed to load installation detail.'));
    } finally {
      setLoading(false);
    }
  }, [modelId]);

  useEffect(() => {
    void reload();
  }, [reload]);

  const handleChanged = async () => {
    await reload();
    await onChanged?.();
  };

  if (loading) {
    return <p className="text-sm text-gray-600">Loading installation detail…</p>;
  }

  if (error) {
    return <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div>;
  }

  if (!detail) {
    return null;
  }

  const isCurated = !!detail.catalogId;

  return (
    <div className="space-y-4">
      <AliasPresetSavePanel
        alias={detail.routerModelId}
        routerEntry={routerEntry}
        fallbackPreset={detail.routerPresetSnapshot}
        onSaved={handleChanged}
      />

      <ModelChatBehaviorPanel
        detail={detail}
        sharedProfileModelCount={sharedProfileModelCount}
        onChanged={handleChanged}
      />

      <div className="flex flex-wrap gap-2">
        {isCurated ? (
          <TextActionButton tone="primary" onClick={() => setChangeQuantOpen(true)} title="Change quant">
            Change quant
          </TextActionButton>
        ) : null}
      </div>

      <details className="rounded border border-gray-200 bg-gray-50 px-3 py-2">
        <summary className="cursor-pointer text-sm font-medium text-gray-800">Installation details</summary>
        <div className="mt-3 space-y-3">
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-2 text-xs text-gray-700">
            {detail.catalogId ? (
              <>
                <dt className="text-gray-500">Curated ID</dt>
                <dd className="font-mono">{detail.catalogId}</dd>
                <dt className="text-gray-500">Catalog version</dt>
                <dd className="font-mono">{detail.catalogVersion ?? '—'}</dd>
              </>
            ) : null}
            <dt className="text-gray-500">Quant</dt>
            <dd className="font-mono">{detail.quantLabel ?? detail.quantId ?? '—'}</dd>
            <dt className="text-gray-500">Repository</dt>
            <dd className="font-mono">{detail.repository ?? '—'}</dd>
            <dt className="text-gray-500">Resolved revision</dt>
            <dd className="font-mono break-all">{detail.resolvedRevision ?? '—'}</dd>
            <dt className="text-gray-500">Router alias</dt>
            <dd className="font-mono">{detail.routerModelId}</dd>
            <dt className="text-gray-500">Target directory</dt>
            <dd className="font-mono">{detail.targetDirectory}</dd>
            <dt className="text-gray-500">Runtime state</dt>
            <dd className="font-mono">
              {detail.runtimeState}
              {detail.loaded ? ' (loaded)' : ''}
            </dd>
          </dl>

          <div className="space-y-2">
            <div className="text-xs font-medium uppercase tracking-wide text-gray-600">Model artifacts</div>
            <ol className="list-decimal space-y-1 pl-4 font-mono text-xs text-gray-800">
              {detail.modelArtifacts.map((artifact) => (
                <li key={artifact.repositoryPath}>
                  {artifact.repositoryPath}
                  {artifact.byteSize != null ? ` (${formatBytes(artifact.byteSize)})` : ''}
                </li>
              ))}
            </ol>
            {detail.projectorArtifacts.length > 0 ? (
              <>
                <div className="text-xs font-medium uppercase tracking-wide text-gray-600">Projector artifacts</div>
                <ol className="list-decimal space-y-1 pl-4 font-mono text-xs text-gray-800">
                  {detail.projectorArtifacts.map((artifact) => (
                    <li key={artifact.repositoryPath}>{artifact.repositoryPath}</li>
                  ))}
                </ol>
              </>
            ) : null}
          </div>
        </div>
      </details>

      <ChangeQuantModal
        isOpen={changeQuantOpen}
        detail={detail}
        onClose={() => setChangeQuantOpen(false)}
        onCompleted={async () => {
          setChangeQuantOpen(false);
          await handleChanged();
        }}
      />
    </div>
  );
}
