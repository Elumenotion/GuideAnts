import { useEffect, useRef, useState } from 'react';
import { FaCheck, FaRedo, FaSpinner, FaTimes } from 'react-icons/fa';
import type { AddModelErrorDto, LlamaRuntimeInventoryItemDto, SettingsModelDto, SettingsRuntimeProfileDto } from '../../../../types/settings';
import {
  LLAMA_MMPROJ_ROLE_ID,
  LLAMA_MODEL_ROLE_ID,
  RepositoryFilePicker,
  llamaCppClassifier,
} from '../../../../pages/settings/editors/common';
import type { RolePickerSpec } from '../../../../pages/settings/editors/common';
import type { LocalAiInstallFormData } from '../useLocalAiWizardState';
import type { LocalAiModelDraft } from '../types';
import {
  isLocalModelOnboardingInFlight,
  localModelOnboardingProgressStep,
} from '../../../../features/localModelOnboarding/status';
import { selectAttachableAliases } from '../../../../features/localModelOnboarding/selectors';

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

export function DraftProgress({ draft }: { draft: LocalAiModelDraft }) {
  if (draft.asyncStatus === 'submitted') {
    return (
      <span className="flex items-center gap-1 text-xs text-blue-700">
        <FaSpinner className="animate-spin" /> Submitting to server…
      </span>
    );
  }
  if (draft.asyncStatus === 'error') {
    return <span className="text-xs text-red-700">{draft.asyncError ?? 'Installation failed'}</span>;
  }
  if (draft.asyncStatus === 'completed') {
    return (
      <span className="flex items-center gap-1 text-xs text-emerald-700">
        <FaCheck className="text-emerald-600" /> Installed
        {draft.setAsGlobalDefault ? <span className="ml-1 text-gray-500">(set as default)</span> : null}
      </span>
    );
  }

  const currentStep = localModelOnboardingProgressStep(draft.asyncStatus);
  const currentIndex = ADD_STEPS.findIndex((s) => s.id === currentStep);
  const pct = draft.asyncProgress != null
    ? Math.round(Math.min(1, Math.max(0, draft.asyncProgress)) * 100)
    : null;

  return (
    <div className="mt-2 space-y-2.5">
      <div className="flex items-center gap-1 text-sm">
        {ADD_STEPS.map((s, i) => {
          const done = i < currentIndex;
          const active = i === currentIndex;
          const future = i > currentIndex;
          return (
            <span key={s.id} className="flex items-center gap-1">
              {i > 0 ? <span className={`mx-0.5 text-xs ${future ? 'text-gray-300' : 'text-gray-400'}`}>&rsaquo;</span> : null}
              {active ? <FaSpinner className="inline animate-spin text-blue-600" /> : null}
              {done ? <FaCheck className="inline text-emerald-500" /> : null}
              <span className={
                active ? 'font-medium text-blue-700'
                : done ? 'text-gray-700'
                : 'text-gray-400'
              }>
                {s.label}
              </span>
            </span>
          );
        })}
      </div>
      {pct != null && draft.asyncStatus === 'downloading' ? (
        <div>
          <div className="mb-1 text-xs text-gray-500">{pct}%</div>
          <div className="h-2 w-full overflow-hidden rounded-full bg-gray-200">
            <div
              className="h-full rounded-full bg-blue-500 transition-all"
              style={{ width: `${pct}%` }}
            />
          </div>
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
  installError: string | null;
  installModelError: AddModelErrorDto | null;
  onInstall: (formData: LocalAiInstallFormData) => Promise<void>;
  onRemoveDraft: (localId: string) => void;
}

export function LocalAiModelsStep({
  draftModels,
  existingModels,
  profiles,
  profilesLoading,
  inventory,
  inventoryLoading,
  installError,
  installModelError,
  onInstall,
  onRemoveDraft,
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

  const totalInstalled = existingModels.length + draftModels.filter((d) => d.asyncStatus === 'completed').length;
  const hasInflightInstall = draftModels.some((d) => isLocalModelOnboardingInFlight(d.asyncStatus));
  const isFirstModel = totalInstalled === 0 && !hasInflightInstall;
  const [setAsGlobalDefault, setSetAsGlobalDefault] = useState(false);

  const unattachedAliases = selectAttachableAliases(inventory);
  const llamaUnavailable = !inventoryLoading && inventory.length === 0;

  const [submitting, setSubmitting] = useState(false);

  const prevCompletedIds = useRef<Set<string>>(new Set());

  useEffect(() => {
    const currentCompleted = new Set(
      draftModels.filter((d) => d.asyncStatus === 'completed').map((d) => d.localId)
    );
    const hasNew = [...currentCompleted].some((id) => !prevCompletedIds.current.has(id));
    prevCompletedIds.current = currentCompleted;
    if (hasNew) {
      resetForm();
    }
  }, [draftModels]);

  const resetForm = () => {
    setRouterModelId('');
    setRepository('');
    setQuantPattern('');
    setMmprojPattern('');
    setTargetDirectory('');
    setExistingAlias('');
    setCatalogModelId('');
    setCatalogDisplayName('');
    setContextSize('');
    setCacheRam('');
    setSetAsGlobalDefault(false);
  };

  const populateFormFromDraft = (draft: LocalAiModelDraft) => {
    setInstallSource(draft.installSource);
    setRuntimeProfileId(draft.runtimeProfileId);
    setRouterModelId(draft.routerModelId);
    setRepository(draft.huggingFaceRepository);
    setQuantPattern(draft.huggingFaceQuantIncludePattern);
    setMmprojPattern(draft.huggingFaceMmprojIncludePattern);
    setTargetDirectory(draft.huggingFaceTargetDirectory);
    setExistingAlias(draft.existingAliasRouterModelId);
    setContextSize(draft.routerContextSize);
    setCacheRam(draft.routerCacheRamMib);
    setCatalogModelId(draft.catalogModelId);
    setCatalogDisplayName(draft.catalogDisplayName);
    setSetAsGlobalDefault(draft.setAsGlobalDefault);
  };

  const handleRetry = (draft: LocalAiModelDraft) => {
    populateFormFromDraft(draft);
    onRemoveDraft(draft.localId);
  };

  const handleInstall = async () => {
    const effectiveCatalogModelId = catalogModelId.trim() || (installSource === 'existingAlias' ? existingAlias : routerModelId);
    const formData: LocalAiInstallFormData = {
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
      catalogModelId: effectiveCatalogModelId,
      catalogDisplayName: catalogDisplayName.trim() || effectiveCatalogModelId,
      setAsGlobalDefault: isFirstModel || setAsGlobalDefault,
    };

    setSubmitting(true);
    try {
      await onInstall(formData);
    } finally {
      setSubmitting(false);
    }
  };

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
        <div className="space-y-2">
          <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Installing</div>
          <div className="space-y-3">
            {draftModels.map((draft) => (
              <div key={draft.localId} className="rounded-lg border border-gray-200 bg-white px-4 py-3 shadow-sm">
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0 flex-1">
                    <div className="font-mono text-sm font-medium text-gray-900">{draft.catalogModelId || draft.routerModelId}</div>
                    <div className="mt-0.5 text-xs text-gray-500">
                      {draft.installSource === 'existingAlias'
                        ? `Alias: ${draft.existingAliasRouterModelId}`
                        : draft.huggingFaceRepository}
                    </div>
                    <DraftProgress draft={draft} />
                  </div>
                  {draft.asyncStatus === 'error' ? (
                    <div className="mt-0.5 flex shrink-0 items-center gap-1">
                      <button
                        type="button"
                        onClick={() => handleRetry(draft)}
                        className="rounded p-1 text-gray-400 hover:bg-blue-50 hover:text-blue-600"
                        title="Retry"
                      >
                        <FaRedo />
                      </button>
                      <button
                        type="button"
                        onClick={() => onRemoveDraft(draft.localId)}
                        className="rounded p-1 text-gray-400 hover:bg-red-50 hover:text-red-600"
                        title="Dismiss"
                      >
                        <FaTimes />
                      </button>
                    </div>
                  ) : null}
                </div>
              </div>
            ))}
          </div>
        </div>
      ) : null}

      <div className="space-y-4 rounded border border-gray-200 bg-gray-50 p-4">
        <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Install a model</div>

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
                  {inventoryLoading ? 'Loading inventory…' : unattachedAliases.length === 0 ? 'No unregistered aliases available' : 'Select alias'}
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
                setQuantPattern(values[LLAMA_MODEL_ROLE_ID] ?? '');
                setMmprojPattern(values[LLAMA_MMPROJ_ROLE_ID] ?? '');
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
                Folder under <span className="font-mono">/models-local/llama</span> where files will be written. Defaults to the router alias.
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

        {isFirstModel ? (
          <p className="text-xs text-gray-500">This model will be set as the global default chat model.</p>
        ) : (
          <label className="flex items-center gap-2 text-sm text-gray-700">
            <input
              type="checkbox"
              checked={setAsGlobalDefault}
              onChange={(e) => setSetAsGlobalDefault(e.target.checked)}
              className="h-4 w-4 rounded border-gray-300 text-blue-600"
            />
            Set as global default chat model
          </label>
        )}

        {installError ? (
          <p className="text-xs text-red-600">{installError}</p>
        ) : null}

        {installModelError ? (
          <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
            <div className="font-medium">{installModelError.code}</div>
            <div>{installModelError.message}</div>
            {installModelError.remediation ? <div className="mt-1">{installModelError.remediation}</div> : null}
          </div>
        ) : null}

        <button
          type="button"
          onClick={() => void handleInstall()}
          disabled={submitting}
          className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-60"
        >
          {submitting ? (
            <span className="flex items-center gap-1.5">
              <FaSpinner className="animate-spin" /> Installing…
            </span>
          ) : 'Install model'}
        </button>
      </div>

      {existingModels.length === 0 && draftModels.length === 0 ? (
        <p className="text-xs text-amber-700">At least one model must be installed to continue.</p>
      ) : null}
    </div>
  );
}
