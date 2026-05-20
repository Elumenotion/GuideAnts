import { useCallback, useEffect, useMemo, useState } from 'react';
import { FaSpinner } from 'react-icons/fa';
import { api } from '../../../../services/api';
import {
  AddModelErrorDto,
  AddModelResponse,
  CreateRuntimeProfileRequest,
  LlamaRuntimeInventoryItemDto,
  SettingsRuntimeProfileDto,
} from '../../../../types/settings';
import { ActiveAddOperationState, AddModelProvider, AddModelWizardState } from '../../types';
import { buildAddModelRequest, createEmptyAddModelWizardState, getErrorMessage } from '../../utils';
import { getCatalogProviderDisplayName } from '../../constants/displayLabels';
import { HIDDEN_CHAT_MODEL_PROVIDERS } from '../../constants/connectionSections';
import { TextActionButton } from '../shared/ActionButtons';
import { SettingsModal } from '../shared/SettingsModal';
import { AnthropicAddForm } from './providers/AnthropicForm';
import { AzureOpenAiChatAddForm } from './providers/AzureOpenAiChatForm';
import { AzureOpenAiResponsesAddForm } from './providers/AzureOpenAiResponsesForm';
import { GoogleGeminiAddForm } from './providers/GoogleGeminiForm';
import { HuggingFaceInferenceAddForm } from './providers/HuggingFaceInferenceForm';
import { LlamaCppAddForm } from './providers/LlamaCppForm';
import { OpenAiChatAddForm } from './providers/OpenAiChatForm';
import { OpenAiResponsesAddForm } from './providers/OpenAiResponsesForm';
import { OpenRouterAddForm } from './providers/OpenRouterForm';
import { KnownCloudModel, ModelIdTypeahead } from './ModelIdTypeahead';
import {
  localModelOnboardingProgressStep,
} from '../../../../features/localModelOnboarding/status';
import { useLocalModelOnboardingOperation } from '../../../../features/localModelOnboarding/useOperationPolling';

const ADD_MODEL_STEPS = [
  { id: 'queued', label: 'Queued', help: 'Waiting for install worker.' },
  { id: 'resolvingFiles', label: 'Resolving files', help: 'Looking up repository artifacts.' },
  { id: 'downloading', label: 'Downloading', help: 'Downloading GGUF and mmproj bytes.' },
  { id: 'registeringAlias', label: 'Registering alias', help: 'Writing router alias mapping.' },
  { id: 'completed', label: 'Completed', help: 'Model is ready for runtime operations.' },
] as const;

const CATALOG_PROVIDER_OPTIONS: readonly AddModelProvider[] = [
  'openai-chat',
  'openai-responses',
  'azure-openai-chat',
  'azure-openai-responses',
  'anthropic',
  'llama-cpp',
  'google-gemini-chat',
  'hf-inference-chat',
  'openrouter-chat',
];

const VISIBLE_CATALOG_PROVIDER_OPTIONS = CATALOG_PROVIDER_OPTIONS.filter(
  (provider) => !HIDDEN_CHAT_MODEL_PROVIDERS.has(provider)
);

interface AddModelWizardProps {
  isOpen: boolean;
  providerPreselect: string | null;
  profiles: SettingsRuntimeProfileDto[];
  profilesLoading: boolean;
  inventory: LlamaRuntimeInventoryItemDto[];
  inventoryError?: string | null;
  onClose: () => void;
  onCreateRuntimeProfileTemplate: (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => Promise<void>;
  onCreateCustomRuntimeProfile: (request: CreateRuntimeProfileRequest) => Promise<SettingsRuntimeProfileDto>;
  onCatalogChanged: () => Promise<void>;
  onSetActiveAddOperation: (value: ActiveAddOperationState | null) => void;
}

function renderProviderForm(
  value: AddModelWizardState,
  onChange: (updates: Partial<AddModelWizardState>) => void,
  profiles: SettingsRuntimeProfileDto[],
  profilesLoading: boolean,
  inventory: LlamaRuntimeInventoryItemDto[],
  inventoryError: string | null | undefined,
  onCreateRuntimeProfileTemplate: (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => Promise<void>,
  onCreateCustomRuntimeProfile: (request: CreateRuntimeProfileRequest) => Promise<SettingsRuntimeProfileDto>
) {
  const props = {
    value,
    onChange,
    profiles,
    profilesLoading,
    inventory,
    inventoryError,
    onCreateRuntimeProfile: onCreateRuntimeProfileTemplate,
    onCreateCustomRuntimeProfile,
  };
  switch (value.provider) {
    case 'openai-chat':
      return <OpenAiChatAddForm {...props} />;
    case 'openai-responses':
      return <OpenAiResponsesAddForm {...props} />;
    case 'azure-openai-chat':
      return <AzureOpenAiChatAddForm {...props} />;
    case 'azure-openai-responses':
      return <AzureOpenAiResponsesAddForm {...props} />;
    case 'anthropic':
      return <AnthropicAddForm {...props} />;
    case 'llama-cpp':
      return <LlamaCppAddForm {...props} />;
    case 'google-gemini-chat':
      return <GoogleGeminiAddForm {...props} />;
    case 'hf-inference-chat':
      return <HuggingFaceInferenceAddForm {...props} />;
    case 'openrouter-chat':
      return <OpenRouterAddForm {...props} />;
    default:
      return <p className="text-sm text-gray-600">Pick a provider to continue.</p>;
  }
}

function parseAddModelError(error: unknown): AddModelErrorDto | null {
  if (!error || typeof error !== 'object') {
    return null;
  }
  const body = (error as { body?: unknown }).body;
  if (!body || typeof body !== 'object') {
    return null;
  }
  const candidate = body as Partial<AddModelErrorDto>;
  if (typeof candidate.code !== 'string' || typeof candidate.step !== 'string' || typeof candidate.message !== 'string') {
    return null;
  }
  return {
    code: candidate.code,
    step: candidate.step,
    message: candidate.message,
    remediation: typeof candidate.remediation === 'string' ? candidate.remediation : undefined,
  };
}

function AddOperationProgress({
  currentStatus,
  progress,
  error,
}: {
  currentStatus: string;
  progress: number | null;
  error: AddModelErrorDto | null;
}) {
  const step = localModelOnboardingProgressStep(currentStatus);
  const currentIndex = ADD_MODEL_STEPS.findIndex((item) => item.id === step);
  return (
    <div className="space-y-3">
      {ADD_MODEL_STEPS.map((item) => {
        const itemIndex = ADD_MODEL_STEPS.findIndex((entry) => entry.id === item.id);
        const reached = itemIndex <= currentIndex;
        const active = item.id === step;
        return (
          <div key={item.id} className="space-y-1 text-sm">
            <div className="flex items-center gap-2">
            {active && currentStatus !== 'completed' ? <FaSpinner className="animate-spin text-blue-600" /> : null}
            <span
              className={
                reached
                  ? active
                    ? 'font-medium text-blue-700'
                    : 'text-gray-900'
                  : 'text-gray-400'
              }
            >
              {item.label}
            </span>
            </div>
            {reached ? <div className="pl-6 text-xs text-gray-500">{item.help}</div> : null}
            {item.id === 'downloading' && active ? (
              <div className="pl-6">
                <div className="h-2 w-full overflow-hidden rounded bg-gray-200">
                  <div
                    className="h-full bg-blue-500 transition-all"
                    style={{ width: `${Math.max(0, Math.min(1, progress ?? 0)) * 100}%` }}
                  />
                </div>
                <div className="mt-1 text-xs text-gray-500">{Math.round(Math.max(0, Math.min(1, progress ?? 0)) * 100)}%</div>
              </div>
            ) : null}
          </div>
        );
      })}
      {error ? (
        <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          <div className="font-medium">{error.code}</div>
          <div>{error.message}</div>
          {error.remediation ? <div className="mt-1 text-xs">{error.remediation}</div> : null}
        </div>
      ) : null}
    </div>
  );
}

export function AddModelWizard({
  isOpen,
  providerPreselect,
  profiles,
  profilesLoading,
  inventory,
  inventoryError,
  onClose,
  onCreateRuntimeProfileTemplate,
  onCreateCustomRuntimeProfile,
  onCatalogChanged,
  onSetActiveAddOperation,
}: AddModelWizardProps) {
  const [step, setStep] = useState<'provider' | 'catalog' | 'providerConfig' | 'review' | 'progress'>('provider');
  const [value, setValue] = useState<AddModelWizardState>(() => createEmptyAddModelWizardState(providerPreselect));
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [operationId, setOperationId] = useState<string | null>(null);
  const [operationStatus, setOperationStatus] = useState<string>('downloading');
  const [operationProgress, setOperationProgress] = useState<number | null>(null);
  const [operationError, setOperationError] = useState<AddModelErrorDto | null>(null);
  const [modelIdError, setModelIdError] = useState<string | null>(null);
  const [checkingModelId, setCheckingModelId] = useState(false);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    const next = createEmptyAddModelWizardState(providerPreselect);
    if (next.provider && HIDDEN_CHAT_MODEL_PROVIDERS.has(next.provider)) {
      next.provider = '';
    }
    if (next.provider) {
      setStep('catalog');
    } else {
      setStep('provider');
    }
    setValue(next);
    setSubmitError(null);
    setSubmitting(false);
    setOperationId(null);
    setOperationStatus('downloading');
    setOperationProgress(null);
    setOperationError(null);
    setModelIdError(null);
    setCheckingModelId(false);
  }, [isOpen, providerPreselect]);

  const handleOperationUpdate = useCallback((op: { status: string; progress?: number | null; error?: AddModelErrorDto | null }) => {
    setOperationStatus(op.status);
    setOperationProgress(typeof op.progress === 'number' ? op.progress : null);
    setOperationError(op.error ?? null);
  }, []);

  const handleOperationTerminal = useCallback((op: { status: string }) => {
    onSetActiveAddOperation(null);
    if (op.status === 'completed') {
      void onCatalogChanged();
    }
  }, [onCatalogChanged, onSetActiveAddOperation]);

  const handleOperationPollFailureThreshold = useCallback(() => {
    setOperationError({
      code: 'INSTALL_STEP_FAILED',
      step: 'downloading',
      message: 'Failed to poll operation status.',
    });
  }, []);

  useLocalModelOnboardingOperation({
    operationId,
    onUpdate: handleOperationUpdate,
    onTerminal: handleOperationTerminal,
    onPollFailureThreshold: handleOperationPollFailureThreshold,
    intervalMs: 2000,
  });

  const canContinueFromProvider = value.provider.trim().length > 0;
  const canContinueFromCatalog = useMemo(() => {
    return (
      value.catalogModelId.trim().length > 0 &&
      value.catalogDisplayName.trim().length > 0 &&
      !modelIdError &&
      !checkingModelId
    );
  }, [checkingModelId, modelIdError, value.catalogDisplayName, value.catalogModelId]);

  const validateModelId = async () => {
    const candidate = value.catalogModelId.trim();
    if (!candidate) {
      setModelIdError(null);
      return;
    }
    setCheckingModelId(true);
    try {
      const models = await api.settings.getModels();
      const taken = models.some((row) => row.modelId === candidate);
      setModelIdError(taken ? `Model id '${candidate}' already exists.` : null);
    } catch {
      setModelIdError('Could not validate model id right now.');
    } finally {
      setCheckingModelId(false);
    }
  };

  const submit = async () => {
    setSubmitting(true);
    setSubmitError(null);
    try {
      const request = buildAddModelRequest(value);
      const response = (await api.settings.addModel(request)) as AddModelResponse;
      if (response.addOperation.kind === 'sync') {
        await onCatalogChanged();
        onSetActiveAddOperation(null);
        onClose();
        return;
      }
      if (!response.operationId) {
        throw new Error('Missing operation id for async add operation.');
      }
      const routerModelId =
        value.provider === 'llama-cpp'
          ? value.llamaInstallSource === 'existingAlias'
            ? value.llamaExistingAliasRouterModelId.trim()
            : value.llamaRouterModelId.trim()
          : '';
      const activeState: ActiveAddOperationState = {
        operationId: response.operationId,
        routerModelId,
        catalogModelId: value.catalogModelId.trim(),
      };
      setOperationId(response.operationId);
      setStep('progress');
      setOperationStatus(response.addOperation.status || 'downloading');
      setOperationProgress(null);
      setOperationError(response.addOperation.error ?? null);
      onSetActiveAddOperation(activeState);
    } catch (error) {
      const structured = parseAddModelError(error);
      if (structured) {
        setOperationError(structured);
        setSubmitError(structured.message);
      } else {
        setSubmitError(getErrorMessage(error, 'Failed to start add-model operation.'));
      }
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <SettingsModal
      isOpen={isOpen}
      title="Add Model"
      onClose={onClose}
      maxWidthClass="max-w-3xl"
      disableDismiss={submitting}
      disableOverlayDismiss
      footer={
        step === 'progress' ? (
          <TextActionButton tone="neutral" onClick={onClose} title="Close wizard">
            Close
          </TextActionButton>
        ) : (
          <>
            <TextActionButton tone="neutral" onClick={onClose} title="Cancel add model">
              Cancel
            </TextActionButton>
            {step !== 'provider' ? (
              <TextActionButton
                tone="neutral"
                onClick={() =>
                  setStep((previous) =>
                    previous === 'catalog' ? 'provider' : previous === 'providerConfig' ? 'catalog' : 'providerConfig'
                  )
                }
                title="Back"
              >
                Back
              </TextActionButton>
            ) : null}
            {step === 'provider' ? (
              <TextActionButton
                tone="primary"
                disabled={!canContinueFromProvider}
                onClick={() => setStep('catalog')}
                title="Continue"
              >
                Continue
              </TextActionButton>
            ) : null}
            {step === 'catalog' ? (
              <TextActionButton
                tone="primary"
                disabled={!canContinueFromCatalog}
                onClick={() => setStep('providerConfig')}
                title="Continue"
              >
                Continue
              </TextActionButton>
            ) : null}
            {step === 'providerConfig' ? (
              <TextActionButton tone="primary" onClick={() => setStep('review')} title="Continue">
                Continue
              </TextActionButton>
            ) : null}
            {step === 'review' ? (
              <TextActionButton
                tone="primary"
                icon={submitting ? <FaSpinner className="animate-spin" /> : undefined}
                disabled={submitting}
                onClick={() => void submit()}
                title="Create model"
              >
                Create model
              </TextActionButton>
            ) : null}
          </>
        )
      }
    >
      <div className="mb-4 text-xs text-gray-500">
        Step:{' '}
        {step === 'provider'
          ? '1 of 5 - Choose provider'
          : step === 'catalog'
          ? '2 of 5 - Catalog entry'
          : step === 'providerConfig'
          ? '3 of 5 - Provider configuration'
          : step === 'review'
          ? '4 of 5 - Review and create'
          : '5 of 5 - Progress'}
      </div>

      {step === 'provider' ? (
        <div className="space-y-2">
          <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Provider</label>
          <select
            value={value.provider}
            onChange={(event) => {
              const provider = event.target.value as AddModelProvider;
              setValue((previous) => ({
                ...previous,
                provider,
              }));
            }}
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          >
            <option value="">Select provider</option>
            {VISIBLE_CATALOG_PROVIDER_OPTIONS.map((p) => (
              <option key={p} value={p}>
                {getCatalogProviderDisplayName(p)}
              </option>
            ))}
          </select>
        </div>
      ) : null}

      {step === 'catalog' ? (
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div className="space-y-2">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Model ID</label>
            <ModelIdTypeahead
              provider={value.provider}
              value={value.catalogModelId}
              onChange={(next) => {
                setModelIdError(null);
                setValue((previous) => ({ ...previous, catalogModelId: next }));
              }}
              onSelectSuggestion={(suggestion: KnownCloudModel) => {
                setModelIdError(null);
                setValue((previous) => ({
                  ...previous,
                  catalogModelId: suggestion.modelId,
                  catalogDisplayName: suggestion.displayName,
                  catalogDescription: suggestion.description ?? '',
                  runtimeProfileId: suggestion.runtimeProfileId ?? '',
                }));
              }}
              onBlur={() => void validateModelId()}
              hasError={!!modelIdError}
            />
            {checkingModelId ? <p className="text-xs text-gray-500">Validating id…</p> : null}
            {modelIdError ? <p className="text-xs text-red-700">{modelIdError}</p> : null}
          </div>
          <div className="space-y-2">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Display Name</label>
            <input
              type="text"
              value={value.catalogDisplayName}
              onChange={(event) => setValue((previous) => ({ ...previous, catalogDisplayName: event.target.value }))}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>
          <div className="space-y-2 md:col-span-2">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Description</label>
            <textarea
              value={value.catalogDescription}
              onChange={(event) => setValue((previous) => ({ ...previous, catalogDescription: event.target.value }))}
              rows={2}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>
          <div className="space-y-2">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Display Order</label>
            <input
              type="number"
              value={value.catalogDisplayOrder}
              onChange={(event) => setValue((previous) => ({ ...previous, catalogDisplayOrder: event.target.value }))}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>
          <label className="inline-flex items-center gap-2 text-sm text-gray-700 md:col-span-2">
            <input
              type="checkbox"
              checked={value.catalogIsActive}
              onChange={(event) => setValue((previous) => ({ ...previous, catalogIsActive: event.target.checked }))}
              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            />
            Active
          </label>
        </div>
      ) : null}

      {step === 'providerConfig' ? (
        <div className="space-y-4">
          {value.provider !== 'llama-cpp' ? (
            <div className="space-y-2">
              <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Runtime Profile</label>
              <select
                value={value.runtimeProfileId}
                onChange={(event) => setValue((previous) => ({ ...previous, runtimeProfileId: event.target.value }))}
                disabled={profilesLoading}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              >
                <option value="">
                  {profilesLoading
                    ? 'Loading profiles...'
                    : profiles.filter((p) => p.providers.includes(value.provider)).length === 0
                    ? `No profiles defined for ${value.provider}`
                    : 'Select runtime profile'}
                </option>
                {profiles
                  .filter((p) => p.providers.includes(value.provider))
                  .map((profile) => (
                    <option key={profile.profileId} value={profile.profileId}>
                      {profile.displayName} ({profile.profileId})
                    </option>
                  ))}
              </select>
              <p className="text-[11px] text-gray-500">
                Assigns sampling parameter controls (Temperature, Top P) to this model in guide and assistant builders.
              </p>
            </div>
          ) : null}
          {renderProviderForm(
            value,
            (updates) => setValue((previous) => ({ ...previous, ...updates })),
            profiles,
            profilesLoading,
            inventory,
            inventoryError,
            onCreateRuntimeProfileTemplate,
            onCreateCustomRuntimeProfile
          )}
        </div>
      ) : null}

      {step === 'review' ? (
        <div className="space-y-3 text-sm">
          <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2">
            <div>
              <strong>Provider:</strong> {getCatalogProviderDisplayName(value.provider)}
            </div>
            <div>
              <strong>Model ID:</strong> {value.catalogModelId}
            </div>
            <div>
              <strong>Display:</strong> {value.catalogDisplayName}
            </div>
            {value.provider === 'llama-cpp' ? (
              <div>
                <strong>Install Source:</strong> {value.llamaInstallSource}
              </div>
            ) : null}
          </div>
          {submitError ? (
            <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-red-700">{submitError}</div>
          ) : null}
        </div>
      ) : null}

      {step === 'progress' ? (
        <div className="space-y-3">
          <AddOperationProgress currentStatus={operationStatus} progress={operationProgress} error={operationError} />
          {operationStatus === 'failed' ? (
            <div className="flex gap-2">
              <TextActionButton
                tone="primary"
                icon={submitting ? <FaSpinner className="animate-spin" /> : undefined}
                disabled={submitting}
                onClick={() => void submit()}
                title="Retry add operation"
              >
                Retry from failed step
              </TextActionButton>
            </div>
          ) : null}
          {operationStatus === 'completed' &&
          value.provider === 'llama-cpp' &&
          (value.llamaInstallSource === 'existingAlias'
            ? value.llamaExistingAliasRouterModelId.trim().length > 0
            : value.llamaRouterModelId.trim().length > 0) ? (
            <div className="flex gap-2">
              <TextActionButton
                tone="primary"
                onClick={() =>
                  void api.settings.loadLlamaModel(
                    value.llamaInstallSource === 'existingAlias'
                      ? value.llamaExistingAliasRouterModelId.trim()
                      : value.llamaRouterModelId.trim()
                  )
                }
                title="Load model now"
              >
                Load now
              </TextActionButton>
            </div>
          ) : null}
        </div>
      ) : null}
    </SettingsModal>
  );
}
