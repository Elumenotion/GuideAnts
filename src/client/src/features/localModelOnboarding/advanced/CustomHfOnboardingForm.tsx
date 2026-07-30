import type { LlamaRuntimeInventoryItemDto } from '../../../types/settings';
import type { AddModelWizardState } from '../../../pages/settings/types';
import { LlamaModelChatBehaviorEditor } from '../../../pages/settings/components/catalog/LlamaModelChatBehaviorEditor';
import { ArtifactGroupPicker } from './ArtifactGroupPicker';
import { AliasPresetEditor } from './AliasPresetEditor';
import { stripPresetRowMetadata } from '../routerPreset';

export interface CustomHfOnboardingFormProps {
  value: AddModelWizardState;
  onChange: (updates: Partial<AddModelWizardState>) => void;
  inventory: LlamaRuntimeInventoryItemDto[];
}

export function CustomHfOnboardingForm({ value, onChange, inventory }: CustomHfOnboardingFormProps) {
  const chosenAlias = value.llamaRouterModelId.trim();
  const chosenAliasTaken = inventory.some(
    (row) => row.routerModelId === chosenAlias && row.catalogModelIds.length > 0
  );

  return (
    <div className="space-y-4">
      <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-950">
        Custom Hugging Face install requires explicit revision, complete artifact group, alias, target directory, alias
        preset, and model chat behavior. Nothing is inferred from runtime profiles.
      </div>

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

      <div className="space-y-1">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Router alias</label>
        <input
          type="text"
          value={value.llamaRouterModelId}
          onChange={(event) => onChange({ llamaRouterModelId: event.target.value })}
          className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm"
          spellCheck={false}
        />
        {chosenAliasTaken ? <p className="text-xs text-amber-700">Alias already has catalog rows.</p> : null}
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

      <ArtifactGroupPicker
        repository={value.llamaHuggingFaceRepository}
        onRepositoryChange={(next) => onChange({ llamaHuggingFaceRepository: next })}
        resolvedRevision={value.llamaHuggingFaceResolvedRevision}
        onResolvedRevisionChange={(next) => onChange({ llamaHuggingFaceResolvedRevision: next })}
        selectedGroupId={value.llamaHuggingFaceArtifactGroupId}
        onSelectedGroupChange={(groupId, files) =>
          onChange({ llamaHuggingFaceArtifactGroupId: groupId, llamaHuggingFaceModelFiles: files })
        }
        selectedMmproj={value.llamaHuggingFaceMmprojFiles[0] ?? ''}
        onSelectedMmprojChange={(path) =>
          onChange({ llamaHuggingFaceMmprojFiles: path.trim() ? [path.trim()] : [] })
        }
      />

      <div className="space-y-1">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Target directory</label>
        <input
          type="text"
          value={value.llamaHuggingFaceTargetDirectory}
          onChange={(event) => onChange({ llamaHuggingFaceTargetDirectory: event.target.value })}
          className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm"
          placeholder={chosenAlias || '(same as router alias)'}
          spellCheck={false}
        />
      </div>

      <AliasPresetEditor
        rows={value.llamaHuggingFaceRouterPresetRows}
        onChange={(rows) => onChange({ llamaHuggingFaceRouterPresetRows: stripPresetRowMetadata(rows) })}
        alias={chosenAlias}
        presetMode={value.llamaHuggingFacePresetMode}
        onPresetModeChange={(mode) => onChange({ llamaHuggingFacePresetMode: mode })}
      />
    </div>
  );
}
