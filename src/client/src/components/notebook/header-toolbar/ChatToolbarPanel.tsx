import { useCallback, useEffect, useMemo, useState } from 'react';
import { FaCheck, FaCog, FaPlay, FaSpinner, FaStop, FaTimes } from 'react-icons/fa';
import { api } from '../../../services/api';
import { textButtonClassName } from '../../../pages/settings/components/shared/ActionButtons';
import type { ChatPanelProps } from './types';
import { WORKSPACE_CONTROLS_COPY, statusToneClass } from './toolbarFormatters';
import type { ChatDefaultsDto, UpdateChatDefaultsRequest } from '../../../types/settings';
import type { ModelDto } from '../../../types/guides';
import {
  buildChatDefaultsModelChangeRequest,
  buildChatDefaultsUpdateRequest,
  chatDefaultsToConfig,
  normalizeChatModelConfigForModel,
} from '../../chat-model/chatDefaults';
import {
  getConversationModelOverride,
  setConversationModelOverride as storeSetConversationModelOverride,
} from '../../../contexts/conversation/conversationModelOverride';

const OP_POLL_MS = 2_000;

export function ChatToolbarPanel({
  chat,
  projectId,
  notebookId,
  conversationId,
  setInFlight,
  onRefresh,
  assistantIdForLlama,
  onOpenSettings,
  onRequestUnloadConfirm,
  showWorkspaceCopy = true,
}: ChatPanelProps) {
  const [chatDefaults, setChatDefaults] = useState<ChatDefaultsDto | null>(null);
  const [catalogModels, setCatalogModels] = useState<ModelDto[]>([]);
  const [catalogLoaded, setCatalogLoaded] = useState(false);
  // Per-conversation model override (session-local). Initialised from the shared
  // store so a reopened popover reflects the pick that was already made.
  const [conversationModelOverride, setConversationModelOverride] = useState<string | null>(
    () => getConversationModelOverride(conversationId)
  );
  const [chatDefaultsError, setChatDefaultsError] = useState<string | null>(null);
  const hasPendingOp =
    chat.inProgressState &&
    chat.inProgressState !== 'ready' &&
    chat.inProgressState !== 'failed';
  const overrideAllChatModels = chatDefaults?.overrideAllChatModels ?? chat.overrideAllChatModels;
  const currentModelId = chat.effectiveModelId;
  // The list is locked only when override is off AND the effective model comes
  // from the assistant's own definition. When the assistant has no model, the
  // effective model is the global default, so a pick can enable the override
  // and change the default in one action.
  const isDefaultedToGlobalDefault =
    !overrideAllChatModels && chat.effectiveModelSource === 'defaultedTo';
  const modelListLocked = !overrideAllChatModels && !isDefaultedToGlobalDefault;
  const loadButtonLabel = hasPendingOp
    ? 'Switching...'
    : chat.localRuntimeOn
      ? 'Loaded'
      : 'Load model';

  const loadChatDefaults = useCallback(async () => {
    try {
      const dto = await api.settings.chatDefaults.get();
      setChatDefaults(dto);
      setChatDefaultsError(null);
    } catch (error: any) {
      setChatDefaultsError(error?.message ?? 'Failed to load chat defaults.');
    }
  }, []);

  const loadCatalogModels = useCallback(async () => {
    try {
      const models = await api.guides.catalogs.models();
      setCatalogModels(models);
      setCatalogLoaded(true);
    } catch (error: any) {
      setChatDefaultsError(error?.message ?? 'Failed to load catalog models.');
    }
  }, []);

  useEffect(() => {
    void loadChatDefaults();
    void loadCatalogModels();
  }, [loadChatDefaults, loadCatalogModels]);

  const catalogModelById = useMemo(
    () => new Map(catalogModels.map((model) => [model.modelId, model])),
    [catalogModels]
  );

  const conversationOverrideLabel = conversationModelOverride
    ? catalogModelById.get(conversationModelOverride)?.displayName ?? conversationModelOverride
    : null;

  const resolveCatalogModel = async (modelId: string): Promise<ModelDto | undefined> => {
    const existing = catalogModelById.get(modelId);
    if (existing) {
      return existing;
    }

    const models = await api.guides.catalogs.models();
    setCatalogModels(models);
    return models.find((model) => model.modelId === modelId);
  };

  const updateChatDefaultsFromRequest = async (request: UpdateChatDefaultsRequest) => {
    setInFlight(true);
    try {
      const updated = await api.settings.chatDefaults.update(request);
      setChatDefaults(updated);
      setChatDefaultsError(null);
      await onRefresh();
    } catch (error: any) {
      setChatDefaultsError(error?.message ?? 'Failed to update chat defaults.');
    } finally {
      setInFlight(false);
    }
  };

  const updateChatDefaults = async (next: ChatDefaultsDto) => {
    const selectedModel = next.defaultModelId ? await resolveCatalogModel(next.defaultModelId) : undefined;
    const normalizedConfig = normalizeChatModelConfigForModel(chatDefaultsToConfig(next), selectedModel);
    await updateChatDefaultsFromRequest(
      buildChatDefaultsUpdateRequest(next, normalizedConfig, next.overrideAllChatModels)
    );
  };

  const toggleOverrideAllChatModels = async () => {
    const current = chatDefaults ?? await api.settings.chatDefaults.get();
    await updateChatDefaults({
      ...current,
      overrideAllChatModels: !current.overrideAllChatModels,
    });
  };

  const setGlobalModel = async (modelId: string) => {
    if (modelListLocked) return;
    const current = chatDefaults ?? await api.settings.chatDefaults.get();
    const selectedModel = await resolveCatalogModel(modelId);
    // Picking from the list while the assistant has no model of its own sets
    // the global override so the pick takes effect immediately.
    await updateChatDefaultsFromRequest(buildChatDefaultsModelChangeRequest(
      current,
      modelId,
      selectedModel,
      true
    ));
  };

  // --- Per-conversation model override (session-local; not global settings) ---
  // Selecting writes to the shared per-conversation store, which
  // `useConversationActions.sendMessage` reads on the next send. No server call:
  // the override is request-scoped and each turn already records its own model.
  const selectConversationModel = (modelId: string) => {
    setConversationModelOverride(modelId);
    storeSetConversationModelOverride(conversationId, modelId);
  };

  const clearConversationModel = () => {
    setConversationModelOverride(null);
    storeSetConversationModelOverride(conversationId, null);
  };

  const powerOn = async () => {
    setInFlight(true);
    try {
      let op = await api.projects.notebooks.conversations.loadLlamaRuntime(
        projectId,
        notebookId,
        assistantIdForLlama
      );
      for (let i = 0; i < 120; i += 1) {
        if (!op || op.state === 'ready' || op.state === 'failed') break;
        await new Promise((resolve) => setTimeout(resolve, OP_POLL_MS));
        op = await api.projects.notebooks.conversations.pollLlamaRuntimeOperation(
          projectId,
          notebookId,
          op.operationId
        );
      }
      await onRefresh();
    } finally {
      setInFlight(false);
    }
  };

  return (
    <div className="space-y-2">
      {showWorkspaceCopy ? <div className="text-xs text-slate-500">{WORKSPACE_CONTROLS_COPY}</div> : null}
      <div className={`text-sm font-medium ${statusToneClass(chat.status)}`}>
        {chat.effectiveModelDisplayName
          ? `${chat.effectiveModelDisplayName} — ${chat.status}`
          : chat.summary}
      </div>
      {chatDefaultsError ? <div className="text-xs text-amber-700">{chatDefaultsError}</div> : null}

      <div className="rounded border border-slate-200 bg-slate-50/60 p-2" data-testid="conversation-model-override">
        <div className="flex items-center justify-between gap-2">
          <span className="text-xs font-semibold text-gray-700">Model for this conversation</span>
          {conversationModelOverride ? (
            <button
              type="button"
              className="text-xs text-slate-500 hover:text-slate-800"
              aria-label="Clear conversation model override"
              title="Revert to the assistant / global default"
              onClick={clearConversationModel}
            >
              <FaTimes className="h-3 w-3" aria-hidden />
              Clear
            </button>
          ) : null}
        </div>
        {conversationModelOverride ? (
          <p className="mt-1 text-xs text-emerald-700">
            <span className="font-medium">{conversationOverrideLabel}</span> — sent with every message in this conversation until cleared.
          </p>
        ) : (
          <p className="mt-1 text-xs text-gray-500">Using the assistant's model (or the global default).</p>
        )}
        <select
          className="mt-2 w-full rounded border border-gray-300 bg-white px-2 py-1.5 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          value={conversationModelOverride ?? ''}
          aria-label="Model for this conversation"
          disabled={!catalogLoaded}
          onChange={(e) => {
            const modelId = e.target.value;
            if (modelId) {
              selectConversationModel(modelId);
            } else {
              clearConversationModel();
            }
          }}
        >
          <option value="">Use assistant's model</option>
          {catalogModels
            .filter((model) => model.isActive)
            .map((model) => (
              <option key={model.modelId} value={model.modelId}>
                {model.displayName}
                {model.description ? ` — ${model.description}` : ''}
              </option>
            ))}
        </select>
      </div>

      <label className="flex cursor-pointer items-start gap-2">
        <input
          type="checkbox"
          className="mt-0.5 h-4 w-4 rounded border-gray-300"
          checked={overrideAllChatModels}
          onChange={() => {
            void toggleOverrideAllChatModels();
          }}
        />
        <span>
          <span className="text-sm font-medium text-gray-900">Override all chat models</span>
          <span className="block text-xs text-gray-500">
            {overrideAllChatModels
              ? 'Global override is on. Model picks below update settings for all chat paths.'
              : isDefaultedToGlobalDefault
                ? 'This assistant has no model of its own, so the global default is in use. Picking a model turns on the override.'
                : 'Using assistant definitions. Turn on override to set a global model.'}
          </span>
        </span>
      </label>

      <div className="max-h-44 overflow-auto space-y-1">
        {chat.modelOptions
          .filter((option) => option.isActive)
          .map((option) => {
            const isCurrent = option.modelId === currentModelId;
            return (
              <button
                key={option.modelId}
                type="button"
                className={`${textButtonClassName('neutral')} w-full justify-start text-left ${
                  modelListLocked ? 'cursor-default' : ''
                } ${isCurrent ? 'ring-2 ring-emerald-400/60 bg-emerald-50 font-medium' : ''}`}
                role="option"
                aria-selected={isCurrent}
                disabled={modelListLocked}
                onClick={() => {
                  void setGlobalModel(option.modelId);
                }}
              >
                {option.displayName} <span className="text-slate-500">({option.provider})</span>
                {isCurrent ? ' ✓' : ''}
              </button>
            );
          })}
      </div>

      {chat.supportsLocalRuntimePower && (
        <div className="mt-2 flex items-center gap-2 border-t pt-2">
          <span className="text-xs text-slate-700">Local model</span>
          <button
            type="button"
            className={`inline-flex items-center gap-1 rounded border px-2 py-1 text-xs font-medium ${
              chat.localRuntimeOn
                ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
                : 'border-emerald-300 bg-white text-emerald-700 hover:bg-emerald-50'
            } disabled:cursor-not-allowed disabled:opacity-70`}
            aria-label={chat.localRuntimeOn ? 'Selected local chat model is loaded' : 'Load selected local chat model'}
            title={chat.localRuntimeOn ? 'Selected local chat model is loaded' : 'Load selected local chat model'}
            disabled={Boolean(hasPendingOp) || chat.localRuntimeOn}
            onClick={() => void powerOn()}
          >
            {hasPendingOp ? (
              <FaSpinner className="h-3.5 w-3.5 animate-spin" />
            ) : chat.localRuntimeOn ? (
              <FaCheck className="h-3.5 w-3.5" />
            ) : (
              <FaPlay className="h-3.5 w-3.5" />
            )}
            {loadButtonLabel}
          </button>
          {chat.localRuntimeOn ? (
            <button
              type="button"
              className="inline-flex items-center gap-1 rounded border border-slate-300 bg-white px-2 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-70"
              aria-label="Unload selected local chat model"
              title="Unload selected local chat model"
              disabled={Boolean(hasPendingOp)}
              onClick={onRequestUnloadConfirm}
            >
              <FaStop className="h-3.5 w-3.5" />
              Unload
            </button>
          ) : null}
        </div>
      )}

      <button
        type="button"
        className="text-blue-600 text-xs inline-flex items-center gap-1 mt-1"
        onClick={onOpenSettings}
      >
        <FaCog className="w-3.5 h-3.5" />
        Open in Settings
      </button>
    </div>
  );
}
