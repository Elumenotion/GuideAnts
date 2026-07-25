import { useEffect, useMemo, useRef, useState } from 'react';
import { FaCheck, FaRedo, FaSpinner, FaTimes } from 'react-icons/fa';
import type { AddModelErrorDto, LlamaRuntimeInventoryItemDto, SettingsModelDto, SettingsRuntimeProfileDto } from '../../../../types/settings';
import type { AddModelWizardState } from '../../../../pages/settings/types';
import type { LocalAiInstallFormData } from '../useLocalAiWizardState';
import type { LocalAiModelDraft } from '../types';
import {
  isLocalModelOnboardingInFlight,
  localModelOnboardingProgressStep,
} from '../../../../features/localModelOnboarding/status';
import { LlamaLocalModelOnboardingPanel } from '../../../../features/localModelOnboarding/curated/LlamaLocalModelOnboardingPanel';
import type { LocalModelOnboardingMode } from '../../../../features/localModelOnboarding/curated/types';
import { CustomHfOnboardingForm } from '../../../../features/localModelOnboarding/advanced/CustomHfOnboardingForm';
import { AttachAliasOnboardingForm } from '../../../../features/localModelOnboarding/advanced/AttachAliasOnboardingForm';
import { stripPresetRowMetadata } from '../../../../features/localModelOnboarding/routerPreset';
import { persistGlobalDefaultModel } from '../utils';

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
      {draft.asyncLogLine ? (
        <div className="font-mono text-xs text-gray-500">{draft.asyncLogLine}</div>
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
  onCuratedInstall: (input: {
    operationId: string;
    catalogModelId: string;
    catalogDisplayName: string;
    routerModelId: string;
    setAsGlobalDefault: boolean;
  }) => void;
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
  onCuratedInstall,
  onRemoveDraft,
}: LocalAiModelsStepProps) {
  const [onboardingMode, setOnboardingMode] = useState<LocalModelOnboardingMode>('curated');
  const [advancedForm, setAdvancedForm] = useState<AddModelWizardState>({
    provider: 'llama-cpp',
    catalogModelId: '',
    catalogDisplayName: '',
    catalogDescription: '',
    catalogDisplayOrder: '',
    catalogIsActive: true,
    runtimeProfileId: '',
    llamaInstallSource: 'huggingface',
    llamaRouterModelId: '',
    llamaHuggingFaceRepository: '',
    llamaHuggingFaceResolvedRevision: '',
    llamaHuggingFaceArtifactGroupId: '',
    llamaHuggingFaceModelFiles: [],
    llamaHuggingFaceMmprojFiles: [],
    llamaHuggingFaceTargetDirectory: '',
    llamaHuggingFaceRouterPresetRows: [],
    llamaHuggingFacePresetMode: 'replace',
    llamaExistingAliasRouterModelId: '',
  });

  const totalInstalled = existingModels.length + draftModels.filter((d) => d.asyncStatus === 'completed').length;
  const hasInflightInstall = draftModels.some((d) => isLocalModelOnboardingInFlight(d.asyncStatus));
  const isFirstModel = totalInstalled === 0 && !hasInflightInstall;
  const [setAsGlobalDefault, setSetAsGlobalDefault] = useState(false);
  const llamaUnavailable = !inventoryLoading && inventory.length === 0;
  const [submitting, setSubmitting] = useState(false);
  const prevCompletedIds = useRef<Set<string>>(new Set());

  const advancedInstallSource = onboardingMode === 'existingAlias' ? 'existingAlias' : 'huggingface';

  const patchAdvancedForm = (updates: Partial<AddModelWizardState>) => {
    setAdvancedForm((previous) => ({
      ...previous,
      ...updates,
      llamaInstallSource: advancedInstallSource,
    }));
  };

  useEffect(() => {
    patchAdvancedForm({ llamaInstallSource: advancedInstallSource });
  }, [advancedInstallSource]);

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
    setAdvancedForm({
      provider: 'llama-cpp',
      catalogModelId: '',
      catalogDisplayName: '',
      catalogDescription: '',
      catalogDisplayOrder: '',
      catalogIsActive: true,
      runtimeProfileId: '',
      llamaInstallSource: advancedInstallSource,
      llamaRouterModelId: '',
      llamaHuggingFaceRepository: '',
      llamaHuggingFaceResolvedRevision: '',
      llamaHuggingFaceArtifactGroupId: '',
      llamaHuggingFaceModelFiles: [],
      llamaHuggingFaceMmprojFiles: [],
      llamaHuggingFaceTargetDirectory: '',
      llamaHuggingFaceRouterPresetRows: [],
      llamaHuggingFacePresetMode: 'replace',
      llamaExistingAliasRouterModelId: '',
    });
    setSetAsGlobalDefault(false);
  };

  const populateFormFromDraft = (draft: LocalAiModelDraft) => {
    if (draft.installSource === 'curated') {
      setOnboardingMode('curated');
    } else {
      setOnboardingMode(draft.installSource === 'existingAlias' ? 'existingAlias' : 'custom');
    }
    setAdvancedForm({
      provider: 'llama-cpp',
      catalogModelId: draft.catalogModelId,
      catalogDisplayName: draft.catalogDisplayName,
      catalogDescription: '',
      catalogDisplayOrder: '',
      catalogIsActive: true,
      runtimeProfileId: draft.runtimeProfileId,
      llamaInstallSource: draft.installSource === 'existingAlias' ? 'existingAlias' : 'huggingface',
      llamaRouterModelId: draft.routerModelId,
      llamaHuggingFaceRepository: draft.huggingFaceRepository,
      llamaHuggingFaceResolvedRevision: draft.huggingFaceResolvedRevision,
      llamaHuggingFaceArtifactGroupId: draft.huggingFaceArtifactGroupId,
      llamaHuggingFaceModelFiles: draft.huggingFaceModelFiles,
      llamaHuggingFaceMmprojFiles: draft.huggingFaceMmprojFiles,
      llamaHuggingFaceTargetDirectory: draft.huggingFaceTargetDirectory,
      llamaHuggingFaceRouterPresetRows: stripPresetRowMetadata(draft.huggingFaceRouterPresetRows),
      llamaHuggingFacePresetMode: draft.huggingFacePresetMode,
      llamaExistingAliasRouterModelId: draft.existingAliasRouterModelId,
    });
    setSetAsGlobalDefault(draft.setAsGlobalDefault);
  };

  const handleRetry = (draft: LocalAiModelDraft) => {
    populateFormFromDraft(draft);
    onRemoveDraft(draft.localId);
  };

  const handleInstall = async () => {
    const formData: LocalAiInstallFormData = {
      installSource: advancedInstallSource,
      routerModelId: advancedInstallSource === 'existingAlias'
        ? advancedForm.llamaExistingAliasRouterModelId
        : advancedForm.llamaRouterModelId,
      runtimeProfileId: advancedForm.runtimeProfileId,
      huggingFaceRepository: advancedForm.llamaHuggingFaceRepository,
      huggingFaceResolvedRevision: advancedForm.llamaHuggingFaceResolvedRevision,
      huggingFaceArtifactGroupId: advancedForm.llamaHuggingFaceArtifactGroupId,
      huggingFaceModelFiles: advancedForm.llamaHuggingFaceModelFiles,
      huggingFaceMmprojFiles: advancedForm.llamaHuggingFaceMmprojFiles,
      huggingFaceTargetDirectory: advancedForm.llamaHuggingFaceTargetDirectory,
      huggingFaceRouterPresetRows: stripPresetRowMetadata(advancedForm.llamaHuggingFaceRouterPresetRows),
      huggingFacePresetMode: advancedForm.llamaHuggingFacePresetMode,
      existingAliasRouterModelId: advancedForm.llamaExistingAliasRouterModelId,
      catalogModelId: advancedForm.catalogModelId,
      catalogDisplayName: advancedForm.catalogDisplayName,
      setAsGlobalDefault: isFirstModel || setAsGlobalDefault,
    };

    setSubmitting(true);
    try {
      await onInstall(formData);
    } finally {
      setSubmitting(false);
    }
  };

  const advancedFormNode = useMemo(() => (
    <div className="space-y-4 rounded border border-gray-200 bg-gray-50 p-4">
      {onboardingMode === 'existingAlias' ? (
        <AttachAliasOnboardingForm
          value={advancedForm}
          onChange={patchAdvancedForm}
          profiles={profiles}
          profilesLoading={profilesLoading}
          inventory={inventory}
          inventoryError={llamaUnavailable ? 'No local llama server is configured.' : null}
        />
      ) : (
        <CustomHfOnboardingForm
          value={advancedForm}
          onChange={patchAdvancedForm}
          profiles={profiles}
          profilesLoading={profilesLoading}
          inventory={inventory}
        />
      )}

      {isFirstModel ? (
        <p className="text-xs text-gray-500">This model will be set as the global default chat model.</p>
      ) : (
        <label className="flex items-center gap-2 text-sm text-gray-700">
          <input
            type="checkbox"
            checked={setAsGlobalDefault}
            onChange={(event) => setSetAsGlobalDefault(event.target.checked)}
            className="h-4 w-4 rounded border-gray-300 text-blue-600"
          />
          Set as global default chat model
        </label>
      )}

      {installError ? <p className="text-xs text-red-600">{installError}</p> : null}
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
  ), [
    advancedForm,
    installError,
    installModelError,
    inventory,
    isFirstModel,
    llamaUnavailable,
    onboardingMode,
    profiles,
    profilesLoading,
    setAsGlobalDefault,
    submitting,
  ]);

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

      <LlamaLocalModelOnboardingPanel
        mode={onboardingMode}
        onModeChange={setOnboardingMode}
        onboardingUi="wizard"
        profiles={profiles}
        profilesLoading={profilesLoading}
        inventory={inventory}
        inventoryError={llamaUnavailable ? 'No local llama server is configured.' : null}
        onCuratedOperationStarted={(operationId, meta) => {
          onCuratedInstall({
            operationId,
            catalogModelId: meta.catalogModelId,
            catalogDisplayName: meta.catalogDisplayName,
            routerModelId: meta.routerModelId,
            setAsGlobalDefault: isFirstModel || setAsGlobalDefault,
          });
        }}
        onCuratedCompleted={() => {
          resetForm();
        }}
        onSetDefault={async (catalogModelIdValue) => {
          await persistGlobalDefaultModel(catalogModelIdValue);
        }}
        advancedForm={advancedFormNode}
      />

      {existingModels.length === 0 && draftModels.length === 0 ? (
        <p className="text-xs text-amber-700">At least one model must be installed to continue.</p>
      ) : null}
    </div>
  );
}
