import { useState } from 'react';
import type { LlamaInstallationDetailDto } from '../../../types/settings';
import { TextActionButton } from '../../../pages/settings/components/shared/ActionButtons';
import { RepairInstallationDialog } from './RepairInstallationDialog';
import { AdoptInstallationDialog } from './AdoptInstallationDialog';

export interface ModelChatBehaviorPanelProps {
  detail: LlamaInstallationDetailDto;
  onChanged?: () => Promise<void>;
}

/** Lifecycle controls for model-owned chat behavior (Repair / Adopt). Field editing lives on the catalog row form. */
export function ModelChatBehaviorPanel({ detail, onChanged }: ModelChatBehaviorPanelProps) {
  const [repairOpen, setRepairOpen] = useState(false);
  const [adoptOpen, setAdoptOpen] = useState(false);
  const isCurated = !!detail.catalogId;

  const handleLifecycleCompleted = async () => {
    await onChanged?.();
  };

  return (
    <div
      data-testid="model-chat-behavior-panel"
      className="flex flex-wrap items-start justify-between gap-3 rounded border border-gray-200 bg-white px-4 py-3"
    >
      <div className="min-w-0 flex-1">
        <h3 className="text-sm font-semibold text-gray-900">Chat behavior</h3>
        <p className="mt-1 text-xs text-gray-600">
          Re-apply curator defaults with Repair or Adopt when the manifest changes. Edit sampling and reasoning in the
          section below.
        </p>
        <dl className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-700">
          <div className="flex gap-1.5">
            <dt className="text-gray-500">Model</dt>
            <dd className="font-mono">{detail.modelId}</dd>
          </div>
          <div className="flex gap-1.5">
            <dt className="text-gray-500">Display name</dt>
            <dd>{detail.catalogModel.displayName}</dd>
          </div>
        </dl>
      </div>

      <div className="flex flex-wrap gap-2">
        <TextActionButton tone="neutral" onClick={() => setRepairOpen(true)} title="Repair from recorded source">
          Repair
        </TextActionButton>
        {isCurated ? (
          <TextActionButton tone="neutral" onClick={() => setAdoptOpen(true)} title="Adopt curated recipe defaults">
            Adopt curated
          </TextActionButton>
        ) : null}
      </div>

      <RepairInstallationDialog
        isOpen={repairOpen}
        detail={detail}
        onClose={() => setRepairOpen(false)}
        onCompleted={async () => {
          setRepairOpen(false);
          await handleLifecycleCompleted();
        }}
      />
      <AdoptInstallationDialog
        isOpen={adoptOpen}
        detail={detail}
        onClose={() => setAdoptOpen(false)}
        onCompleted={async () => {
          setAdoptOpen(false);
          await handleLifecycleCompleted();
        }}
      />
    </div>
  );
}
