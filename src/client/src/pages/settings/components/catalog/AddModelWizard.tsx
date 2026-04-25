import { useEffect, useMemo, useState } from 'react';
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

const ADD_MODEL_STEPS = [
  { id: 'queued', label: 'Queued', help: 'Waiting for install worker.' },
  { id: 'resolvingFiles', label: 'Resolving files', help: 'Looking up repository artifacts.' },
  { id: 'downloading', label: 'Downloading', help: 'Downloading GGUF and mmproj bytes.' },
  { id: 'registeringAlias', label: 'Registering alias', help: 'Writing router alias mapping.' },
  { id: 'registeringCatalog', label: 'Registering catalog', help: 'Creating catalog model row.' },
  { id: 'completed', label: 'Completed', help: 'Model is ready for runtime operations.' },
] as const;

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

function operationStep(status: string): (typeof ADD_MODEL_STEPS)[number]['id'] {
  const normalized = status.trim();
  if (normalized === 'queued') {
    return 'queued';
  }
  if (normalized === 'resolving' || normalized === 'resolvingFiles') {
    return 'resolvingFiles';
  }
  if (normalized === 'downloading') {
    return 'downloading';
  }
  if (normalized === 'registering' || normalized === 'registeringAlias') {
    return 'registeringAlias';
  }
  if (normalized === 'registeringCatalog') {
    return 'registeringCatalog';
  }
  if (normalized === 'completed') {
    return 'completed';
  }
  return 'downloading';
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
  const step = operationStep(currentStatus);
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

  useEffect(() => {
    if (!operationId) {
      return;
    }
    let cancelled = false;
    const run = async () => {
      let delayMs = 1000;
      while (!cancelled) {
        try {
          const op = await api.settings.getDownloadStatus(operationId);
          if (cancelled) {
            return;
          }
          setOperationStatus(op.status);
          setOperationProgress(typeof op.progress === 'number' ? op.progress : null);
          setOperationError(op.error ?? null);
          if (op.status === 'completed') {
            onSetActiveAddOperation(null);
            await onCatalogChanged();
            return;
          }
          if (op.status === 'failed') {
            return;
          }
        } catch (error) {
          if (!cancelled) {
            setOperationError({
              code: 'INSTALL_STEP_FAILED',
              step: 'downloading',
              message: getErrorMessage(error, 'Failed to poll operation status.'),
            });
          }
          return;
        }
        await new Promise((resolve) => window.setTimeout(resolve, delayMs));
        delayMs = Math.min(delayMs * 1.5, 4000);
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [operationId, onCatalogChanged, onSetActiveAddOperation]);

  const canContinueFromProvider = value.provider.trim().length > 0;
  const canContinueFromCatalog = useMemo(() => {
    return (
      value.catalogModelId.trim().length > 0 &&
      value.catalogDisplayName.trim().length > 0 &&
      value.catalogResourceGroupKey.trim().length > 0 &&
      !modelIdError &&
      !checkingModelId
    );
  }, [checkingModelId, modelIdError, value.catalogDisplayName, value.catalogModelId, value.catalogResourceGroupKey]);

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
            onChange={(event) => setValue((previous) => ({ ...previous, provider: event.target.value as AddModelProvider }))}
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          >
            <option value="">Select provider</option>
            <option value="openai-chat">openai-chat</option>
            <option value="openai-responses">openai-responses</option>
            <option value="azure-openai-chat">azure-openai-chat</option>
            <option value="azure-openai-responses">azure-openai-responses</option>
            <option value="anthropic">anthropic</option>
            <option value="llama-cpp">llama-cpp</option>
            <option value="google-gemini-chat">google-gemini-chat</option>
            <option value="hf-inference-chat">hf-inference-chat</option>
            <option value="openrouter-chat">openrouter-chat</option>
          </select>
        </div>
      ) : null}

      {step === 'catalog' ? (
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div className="space-y-2">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Model ID</label>
            <input
              type="text"
              value={value.catalogModelId}
              onChange={(event) => {
                setModelIdError(null);
                setValue((previous) => ({ ...previous, catalogModelId: event.target.value }));
              }}
              onBlur={() => void validateModelId()}
              className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
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
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Resource Group</label>
            <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-700">
              <span className="font-mono">local</span>
              <span className="ml-2 text-xs text-gray-500">Resource grouping is reserved for future multi-pool scheduling.</span>
            </div>
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
        <div>
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
              <strong>Provider:</strong> {value.provider}
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
              <TextActionButton tone="primary" onClick={() => void submit()} title="Retry add operation">
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
