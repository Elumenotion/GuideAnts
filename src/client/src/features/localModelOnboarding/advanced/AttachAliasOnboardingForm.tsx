import { useEffect, useState } from 'react';
import { api } from '../../../services/api';
import type { LlamaRouterEntryDto, LlamaRuntimeInventoryItemDto } from '../../../types/settings';
import type { AddModelWizardState } from '../../../pages/settings/types';
import { LlamaModelChatBehaviorEditor } from '../../../pages/settings/components/catalog/LlamaModelChatBehaviorEditor';
import { selectAttachableAliases } from '../selectors';
import { AliasPresetEditor } from './AliasPresetEditor';
import { presetRowsFromRecord } from '../routerPreset';
import { getErrorMessage } from '../../../pages/settings/utils';

export interface AttachAliasOnboardingFormProps {
  value: AddModelWizardState;
  onChange: (updates: Partial<AddModelWizardState>) => void;
  inventory: LlamaRuntimeInventoryItemDto[];
  inventoryError?: string | null;
}

export function AttachAliasOnboardingForm({
  value,
  onChange,
  inventory,
  inventoryError,
}: AttachAliasOnboardingFormProps) {
  const [routerEntries, setRouterEntries] = useState<LlamaRouterEntryDto[]>([]);
  const [entriesError, setEntriesError] = useState<string | null>(null);
  const attachable = selectAttachableAliases(inventory);
  const selectedAlias = value.llamaExistingAliasRouterModelId.trim();
  const selectedEntry = routerEntries.find((entry) => entry.alias === selectedAlias);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const response = await api.settings.getLlamaRouterEntries();
        if (!cancelled) {
          setRouterEntries(response.entries);
          setEntriesError(null);
        }
      } catch (error) {
        if (!cancelled) {
          setEntriesError(getErrorMessage(error, 'Failed to load router entries.'));
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <div className="space-y-4">
      <div className="rounded border border-blue-200 bg-blue-50 px-3 py-2 text-xs text-blue-950">
        Attach binds a catalog identity and model chat behavior to an existing artifact-backed alias. The alias preset is
        not rewritten.
      </div>

      {inventoryError ? (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900">{inventoryError}</div>
      ) : null}

      <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
        <div className="space-y-1">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Catalog model ID</label>
          <input
            type="text"
            value={value.catalogModelId}
            onChange={(event) => onChange({ catalogModelId: event.target.value })}
            className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm"
            spellCheck={false}
          />
        </div>
        <div className="space-y-1">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Display name</label>
          <input
            type="text"
            value={value.catalogDisplayName}
            onChange={(event) => onChange({ catalogDisplayName: event.target.value })}
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm"
          />
        </div>
      </div>

      <LlamaModelChatBehaviorEditor
        value={{
          samplingParametersJson: value.samplingParametersJson,
          reasoningChoicesJson: value.reasoningChoicesJson,
          thinkingControlJson: value.thinkingControlJson,
          requestFieldsWhenToolsPresentJson: value.requestFieldsWhenToolsPresentJson,
          combineSystemAndDeveloperMessages: value.combineSystemAndDeveloperMessages,
          thoughtBlockPattern: value.thoughtBlockPattern,
        }}
        onChange={onChange}
      />

      <div className="space-y-1">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Unbound alias</label>
        <select
          value={value.llamaExistingAliasRouterModelId}
          onChange={(event) => onChange({ llamaExistingAliasRouterModelId: event.target.value })}
          className="w-full rounded border border-gray-300 px-3 py-2 text-sm"
        >
          <option value="">Select orphaned alias</option>
          {attachable.map((row) => (
            <option key={row.routerModelId} value={row.routerModelId}>
              {row.routerModelId}
            </option>
          ))}
        </select>
        {entriesError ? <p className="text-xs text-red-700">{entriesError}</p> : null}
      </div>

      {selectedEntry ? (
        <AliasPresetEditor
          rows={presetRowsFromRecord(selectedEntry.preset ?? {})}
          onChange={() => undefined}
          alias={selectedEntry.alias}
          readOnly
        />
      ) : selectedAlias ? (
        <p className="text-xs text-amber-700">No router preset found for alias {selectedAlias}.</p>
      ) : null}
    </div>
  );
}
