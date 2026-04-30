import { useCallback, useEffect, useMemo, useState } from 'react';
import { FaCheck, FaCog, FaPlay, FaSpinner, FaStop } from 'react-icons/fa';
import { api } from '../../../services/api';
import { textButtonClassName } from '../../../pages/settings/components/shared/ActionButtons';
import type { ChatPanelProps } from './types';
import { WORKSPACE_CONTROLS_COPY, statusToneClass } from './toolbarFormatters';
import type { ChatDefaultsDto } from '../../../types/settings';
import type { ModelDto } from '../../../types/guides';
import {
  normalizeReasoningEffortForModel,
  normalizeSamplingValueForModel,
} from '../../chat-model/reasoning';

const OP_POLL_MS = 2_000;

export function ChatToolbarPanel({
  chat,
  projectId,
  notebookId,
  setInFlight,
  onRefresh,
  assistantIdForLlama,
  onOpenSettings,
  onRequestUnloadConfirm,
  showWorkspaceCopy = true,
}: ChatPanelProps) {
  const [chatDefaults, setChatDefaults] = useState<ChatDefaultsDto | null>(null);
  const [catalogModels, setCatalogModels] = useState<ModelDto[]>([]);
  const [chatDefaultsError, setChatDefaultsError] = useState<string | null>(null);
  const hasPendingOp =
    chat.inProgressState &&
    chat.inProgressState !== 'ready' &&
    chat.inProgressState !== 'failed';
  const overrideAllChatModels = chatDefaults?.overrideAllChatModels ?? chat.overrideAllChatModels;
  const currentModelId = chat.effectiveModelId;
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

  const normalizeChatDefaults = useCallback((next: ChatDefaultsDto): ChatDefaultsDto => {
    const normalizedModelId = next.defaultModelId ?? null;
    const selectedModel = normalizedModelId ? catalogModelById.get(normalizedModelId) : undefined;
    const modelChanged = normalizedModelId !== (chatDefaults?.defaultModelId ?? null);

    return {
      ...next,
      defaultModelId: normalizedModelId,
      reasoningEffort:
        selectedModel
          ? (normalizeReasoningEffortForModel(selectedModel, next.reasoningEffort) ?? null)
          : modelChanged
            ? null
            : (next.reasoningEffort ?? null),
      temperature: selectedModel
        ? normalizeSamplingValueForModel(selectedModel, 'temperature', next.temperature)
        : modelChanged
          ? null
          : (next.temperature ?? null),
      topP: selectedModel
        ? normalizeSamplingValueForModel(selectedModel, 'top_p', next.topP)
        : modelChanged
          ? null
          : (next.topP ?? null),
    };
  }, [catalogModelById, chatDefaults?.defaultModelId]);

  const updateChatDefaults = async (next: ChatDefaultsDto) => {
    setInFlight(true);
    try {
      const normalized = normalizeChatDefaults(next);
      const updated = await api.settings.chatDefaults.update({
        rowVersion: normalized.rowVersion,
        defaultModelId: normalized.defaultModelId ?? null,
        overrideAllChatModels: normalized.overrideAllChatModels,
        temperature: normalized.temperature ?? null,
        topP: normalized.topP ?? null,
        reasoningEffort: normalized.reasoningEffort ?? null,
        samplingParametersJson: normalized.samplingParametersJson ?? null,
      });
      setChatDefaults(updated);
      setChatDefaultsError(null);
      await onRefresh();
    } catch (error: any) {
      setChatDefaultsError(error?.message ?? 'Failed to update chat defaults.');
    } finally {
      setInFlight(false);
    }
  };

  const toggleOverrideAllChatModels = async () => {
    const current = chatDefaults ?? await api.settings.chatDefaults.get();
    await updateChatDefaults({
      ...current,
      overrideAllChatModels: !current.overrideAllChatModels,
    });
  };

  const setGlobalModel = async (modelId: string) => {
    if (!overrideAllChatModels) return;
    const current = chatDefaults ?? await api.settings.chatDefaults.get();
    await updateChatDefaults({
      ...current,
      defaultModelId: modelId,
      overrideAllChatModels: true,
    });
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
                  overrideAllChatModels ? '' : 'cursor-default'
                } ${isCurrent ? 'ring-2 ring-emerald-400/60 bg-emerald-50 font-medium' : ''}`}
                role="option"
                aria-selected={isCurrent}
                disabled={!overrideAllChatModels}
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
