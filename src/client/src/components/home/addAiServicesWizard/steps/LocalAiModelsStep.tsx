import { useState } from 'react';
import { FaCheck, FaSpinner, FaTimes } from 'react-icons/fa';
import type { AddModelErrorDto, LlamaRuntimeInventoryItemDto, SettingsModelDto, SettingsRuntimeProfileDto } from '../../../../types/settings';
import {
  LLAMA_MMPROJ_ROLE_ID,
  LLAMA_MODEL_ROLE_ID,
  RepositoryFilePicker,
  llamaCppClassifier,
} from '../../../../pages/settings/editors/common';
import type { RolePickerSpec } from '../../../../pages/settings/editors/common';
import type { LocalAiModelDraft } from '../types';

const LLAMA_ROLES: RolePickerSpec[] = [
  {
    id: LLAMA_MODEL_ROLE_ID,
    label: 'Model file (GGUF)',
    hint: 'Exact filename from the repository. Sharded quants are not supported.',
    required: true,
    manualPlaceholder: 'Qwen3.5-9B-Q5_K_M.gguf',
    placeholder: 'Select a GGUF file…',
  },
  {
    id: LLAMA_MMPROJ_ROLE_ID,
    label: 'Vision projector (mmproj)',
    hint: 'Leave blank for text-only models.',
    manualPlaceholder: 'mmproj-F16.gguf',
    placeholder: 'Select a projector file…',
  },
];

const ADD_STEPS = [
  { id: 'queued', label: 'Queued' },
  { id: 'resolvingFiles', label: 'Resolving files' },
  { id: 'downloading', label: 'Downloading' },
  { id: 'registeringAlias', label: 'Registering alias' },
  { id: 'completed', label: 'Completed' },
] as const;

function operationStep(status: string): (typeof ADD_STEPS)[number]['id'] {
  const s = status.trim();
  if (s === 'queued') return 'queued';
  if (s === 'resolving' || s === 'resolvingFiles') return 'resolvingFiles';
  if (s === 'downloading') return 'downloading';
  if (s === 'registering' || s === 'registeringAlias') return 'registeringAlias';
  if (s === 'completed') return 'completed';
  return 'downloading';
}

function DraftProgress({ draft }: { draft: LocalAiModelDraft }) {
  if (draft.asyncStatus === 'pending') {
    return <span className="text-xs text-amber-700">Queued for installation</span>;
  }
  if (draft.asyncStatus === 'error') {
    return <span className="text-xs text-red-700">{draft.asyncError ?? 'Installation failed'}</span>;
  }
  if (draft.asyncStatus === 'completed') {
    return <span className="flex items-center gap-1 text-xs text-emerald-700"><FaCheck className="text-emerald-600" /> Installed</span>;
  }

  const currentStep = operationStep(draft.asyncStatus === 'downloading' ? 'downloading' : draft.asyncStatus);
  const currentIndex = ADD_STEPS.findIndex((s) => s.id === currentStep);

  return (
    <div className="mt-1 space-y-1">
      <div className="flex flex-wrap gap-2">
        {ADD_STEPS.map((s, i) => (
          <span
            key={s.id}
            className={`text-xs ${i <= currentIndex ? 'text-gray-900 font-medium' : 'text-gray-400'} ${s.id === currentStep && draft.asyncStatus !== 'completed' ? 'text-blue-700' : ''}`}
          >
            {i === currentIndex && draft.asyncStatus !== 'completed' ? (
              <FaSpinner className="mr-1 inline animate-spin text-blue-600" />
            ) : null}
            {s.label}
          </span>
        ))}
      </div>
      {draft.asyncProgress != null && draft.asyncStatus === 'downloading' ? (
        <div className="h-1.5 w-full overflow-hidden rounded bg-gray-200">
          <div
            className="h-full bg-blue-500 transition-all"
            style={{ width: `${Math.round(Math.min(1, Math.max(0, draft.asyncProgress)) * 100)}%` }}
          />
        </div>
      ) : null}
    </div>
  );
}

interface LocalAiModelsStepProps {
  draftModels: LocalAiModelDraft[];
  existingModels: SettingsModelDto[];
  profiles: SettingsRuntimeProfileDto[];
  profilesLoading: boolean;
  inventory: LlamaRuntimeInventoryItemDto[];
  inventoryLoading: boolean;
  addError: string | null;
  addModelError: AddModelErrorDto | null;
  onAddDraft: (draft: Omit<LocalAiModelDraft, 'localId' | 'persisted' | 'asyncOperationId' | 'asyncStatus' | 'asyncProgress' | 'asyncError'>) => void;
  onRemoveDraft: (localId: string) => void;
  onInstallDraft: (localId: string) => Promise<void>;
  onCreateRuntimeProfile: (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => Promise<void>;
}

export function LocalAiModelsStep({
  draftModels,
  existingModels,
  profiles,
  profilesLoading,
  inventory,
  inventoryLoading,
  addError,
  addModelError,
  onAddDraft,
  onRemoveDraft,
  onInstallDraft,
}: LocalAiModelsStepProps) {
  const [installSource, setInstallSource] = useState<'huggingface' | 'existingAlias'>('huggingface');
  const [runtimeProfileId, setRuntimeProfileId] = useState('');
  const [routerModelId, setRouterModelId] = useState('');
  const [repository, setRepository] = useState('');
  const [quantPattern, setQuantPattern] = useState('');
  const [mmprojPattern, setMmprojPattern] = useState('');
  const [targetDirectory, setTargetDirectory] = useState('');
  const [existingAlias, setExistingAlias] = useState('');
  const [contextSize, setContextSize] = useState('');
  const [cacheRam, setCacheRam] = useState('');
  const [catalogModelId, setCatalogModelId] = useState('');
  const [catalogDisplayName, setCatalogDisplayName] = useState('');
  const [setAsGlobalDefault, setSetAsGlobalDefault] = useState(existingModels.length === 0 && draftModels.length === 0);

  const unattachedAliases = inventory.filter((row) => row.catalogModelIds.length === 0 && row.hasModelFile);
  const llamaUnavailable = !inventoryLoading && inventory.length === 0;

  const handleAdd = () => {
    onAddDraft({
      installSource,
      routerModelId: installSource === 'existingAlias' ? existingAlias : routerModelId,
      runtimeProfileId,
      huggingFaceRepository: repository,
      huggingFaceQuantIncludePattern: quantPattern,
      huggingFaceMmprojIncludePattern: mmprojPattern,
      huggingFaceTargetDirectory: targetDirectory || routerModelId,
      existingAliasRouterModelId: existingAlias,
      routerContextSize: contextSize,
      routerCacheRamMib: cacheRam,
      catalogModelId: catalogModelId || (installSource === 'existingAlias' ? existingAlias : routerModelId),
      catalogDisplayName: catalogDisplayName || catalogModelId || (installSource === 'existingAlias' ? existingAlias : routerModelId),
      setAsGlobalDefault,
    });
    setRouterModelId('');
    setRepository('');
    setQuantPattern('');
    setMmprojPattern('');
    setTargetDirectory('');
    setExistingAlias('');
    setCatalogModelId('');
    setCatalogDisplayName('');
    setSetAsGlobalDefault(false);
  };

  const totalModels = existingModels.length + draftModels.length;
  const lockDefault = totalModels === 0;
  const effectiveSetAsDefault = lockDefault ? true : setAsGlobalDefault;

  return (
    <div className="space-y-5">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Local Chat Models</h3>
        <p className="mt-1 text-sm text-gray-600">
          Install llama-cpp models from Hugging Face or attach existing runtime aliases. Each model requires
          a runtime profile that controls sampling and reasoning parameters.
        </p>
      </div>

      {existingModels.length > 0 ? (
        <div className="space-y-1">
          <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Already in catalog</div>
          <div className="divide-y divide-gray-100 rounded border border-gray-200">
            {existingModels.map((model) => (
              <div key={model.modelId} className="flex items-center justify-between px-3 py-1.5 text-sm text-gray-700">
                <span className="font-mono">{model.modelId}</span>
                <FaCheck className="text-emerald-600" />
              </div>
            ))}
          </div>
        </div>
      ) : null}

      {draftModels.length > 0 ? (
        <div className="space-y-1">
          <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Queued installs</div>
          <div className="divide-y divide-gray-100 rounded border border-gray-200">
            {draftModels.map((draft) => (
              <div key={draft.localId} className="px-3 py-2">
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="font-mono text-sm text-gray-900">{draft.catalogModelId || draft.routerModelId}</div>
                    <div className="text-xs text-gray-500">
                      {draft.installSource === 'existingAlias'
                        ? `Attach alias: ${draft.existingAliasRouterModelId}`
                        : `HF: ${draft.huggingFaceRepository}`}
                    </div>
                    <DraftProgress draft={draft} />
                  </div>
                  <div className="flex shrink-0 gap-2">
                    {draft.asyncStatus === 'pending' ? (
                      <button
                        type="button"
                        onClick={() => void onInstallDraft(draft.localId)}
                        className="rounded border border-blue-300 px-2 py-1 text-xs text-blue-700 hover:bg-blue-50"
                      >
                        Install
                      </button>
                    ) : null}
                    {draft.asyncStatus === 'pending' || draft.asyncStatus === 'error' ? (
                      <button
                        type="button"
                        onClick={() => onRemoveDraft(draft.localId)}
                        className="text-gray-400 hover:text-red-600"
                        title="Remove"
                      >
                        <FaTimes />
                      </button>
                    ) : null}
                  </div>
                </div>
              </div>
            ))}
          </div>
        </div>
      ) : null}

      <div className="space-y-4 rounded border border-gray-200 bg-gray-50 p-4">
        <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Add a model</div>

        <div className="space-y-1">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Install Source</label>
          <select
            value={installSource}
            onChange={(e) => setInstallSource(e.target.value as 'huggingface' | 'existingAlias')}
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          >
            <option value="huggingface">Install from Hugging Face</option>
            <option value="existingAlias">Attach existing alias</option>
          </select>
        </div>

        <div className="space-y-1">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Runtime Profile</label>
          <select
            value={runtimeProfileId}
            onChange={(e) => setRuntimeProfileId(e.target.value)}
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            disabled={profilesLoading}
          >
            <option value="">{profilesLoading ? 'Loading profiles…' : 'Select runtime profile'}</option>
            {profiles.map((profile) => (
              <option key={profile.profileId} value={profile.profileId}>
                {profile.displayName} ({profile.profileId})
              </option>
            ))}
          </select>
          <p className="text-[11px] text-gray-500">Controls sampling parameters and thinking/reasoning behavior.</p>
        </div>

        {installSource === 'existingAlias' ? (
          <div className="space-y-1">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Existing Alias</label>
            {llamaUnavailable ? (
              <p className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
                No local llama server is reachable. Attach existing alias requires a running llama runtime.
              </p>
            ) : (
              <select
                value={existingAlias}
                onChange={(e) => setExistingAlias(e.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              >
                <option value="">
                  {inventoryLoading ? 'Loading inventory…' : unattachedAliases.length === 0 ? 'No orphaned aliases available' : 'Select alias'}
                </option>
                {unattachedAliases.map((row) => (
                  <option key={row.routerModelId} value={row.routerModelId}>
                    {row.routerModelId}
                  </option>
                ))}
              </select>
            )}
          </div>
        ) : (
          <>
            <div className="space-y-1">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Router Alias</label>
              <input
                type="text"
                value={routerModelId}
                onChange={(e) => setRouterModelId(e.target.value)}
                placeholder="e.g. qwen3-9b"
                className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                autoComplete="off"
                spellCheck={false}
              />
              <p className="text-[11px] text-gray-500">Identifier used by the llama router. Must be unique.</p>
            </div>

            <RepositoryFilePicker
              repository={repository}
              onRepositoryChange={setRepository}
              roles={LLAMA_ROLES}
              classify={llamaCppClassifier}
              onChange={(values) => {
                const nextQuant = values[LLAMA_MODEL_ROLE_ID] ?? '';
                const nextMmproj = values[LLAMA_MMPROJ_ROLE_ID] ?? '';
                setQuantPattern(nextQuant);
                setMmprojPattern(nextMmproj);
              }}
              serviceOrigin="llamaCpp"
              repoInputHint="Paste the owner/repo shown at the top of the Hugging Face model page."
            />

            <div className="space-y-1">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Target Directory</label>
              <input
                type="text"
                value={targetDirectory}
                onChange={(e) => setTargetDirectory(e.target.value)}
                placeholder={routerModelId || '(same as router alias)'}
                className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                autoComplete="off"
                spellCheck={false}
              />
              <p className="text-[11px] text-gray-500">
                Folder under <span className="font-mono">/models-local/llama</span> where the files will be written. Defaults to the router alias.
              </p>
            </div>
          </>
        )}

        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div className="space-y-1">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Catalog Model ID</label>
            <input
              type="text"
              value={catalogModelId}
              onChange={(e) => setCatalogModelId(e.target.value)}
              placeholder={installSource === 'existingAlias' ? existingAlias : routerModelId}
              className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              autoComplete="off"
              spellCheck={false}
            />
            <p className="text-[11px] text-gray-500">Defaults to router alias if left blank.</p>
          </div>
          <div className="space-y-1">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Display Name</label>
            <input
              type="text"
              value={catalogDisplayName}
              onChange={(e) => setCatalogDisplayName(e.target.value)}
              placeholder={catalogModelId || (installSource === 'existingAlias' ? existingAlias : routerModelId)}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              autoComplete="off"
            />
          </div>
        </div>

        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div className="space-y-1">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Context Size (tokens)</label>
            <input
              type="text"
              inputMode="numeric"
              value={contextSize}
              onChange={(e) => setContextSize(e.target.value)}
              placeholder="(container default)"
              className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              autoComplete="off"
            />
          </div>
          <div className="space-y-1">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Cache RAM (MiB)</label>
            <input
              type="text"
              inputMode="numeric"
              value={cacheRam}
              onChange={(e) => setCacheRam(e.target.value)}
              placeholder="(container default)"
              className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              autoComplete="off"
            />
          </div>
        </div>

        {totalModels > 0 ? (
          <label className="flex items-center gap-2 text-sm text-gray-700">
            <input
              type="checkbox"
              checked={effectiveSetAsDefault}
              disabled={lockDefault}
              onChange={(e) => setSetAsGlobalDefault(e.target.checked)}
              className="h-4 w-4 rounded border-gray-300 text-blue-600"
            />
            Set as global default chat model
          </label>
        ) : null}

        {addError ? (
          <p className="text-xs text-red-600">{addError}</p>
        ) : null}

        {addModelError ? (
          <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
            <div className="font-medium">{addModelError.code}</div>
            <div>{addModelError.message}</div>
            {addModelError.remediation ? <div className="mt-1">{addModelError.remediation}</div> : null}
          </div>
        ) : null}

        <button
          type="button"
          onClick={handleAdd}
          className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
        >
          Add to queue
        </button>
      </div>

      {totalModels === 0 ? (
        <p className="text-xs text-amber-700">At least one model must be added to continue.</p>
      ) : null}
    </div>
  );
}
