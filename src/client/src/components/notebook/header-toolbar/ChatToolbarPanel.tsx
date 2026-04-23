import { useCallback, useEffect, useState } from 'react';
import { FaCog, FaPlay, FaSpinner, FaStop } from 'react-icons/fa';
import { api } from '../../../services/api';
import { textButtonClassName } from '../../../pages/settings/components/shared/ActionButtons';
import type { ChatPanelProps } from './types';
import { WORKSPACE_CONTROLS_COPY, statusToneClass } from './toolbarFormatters';
import type { ChatDefaultsDto } from '../../../types/settings';

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
  const [chatDefaultsError, setChatDefaultsError] = useState<string | null>(null);
  const hasPendingOp =
    chat.inProgressState &&
    chat.inProgressState !== 'ready' &&
    chat.inProgressState !== 'failed';
  const overrideAllChatModels = chatDefaults?.overrideAllChatModels ?? chat.overrideAllChatModels;
  const currentModelId = chat.effectiveModelId;

  const loadChatDefaults = useCallback(async () => {
    try {
      const dto = await api.settings.chatDefaults.get();
      setChatDefaults(dto);
      setChatDefaultsError(null);
    } catch (error: any) {
      setChatDefaultsError(error?.message ?? 'Failed to load chat defaults.');
    }
  }, []);

  useEffect(() => {
    void loadChatDefaults();
  }, [loadChatDefaults]);

  const updateChatDefaults = async (next: ChatDefaultsDto) => {
    setInFlight(true);
    try {
      const updated = await api.settings.chatDefaults.update({
        rowVersion: next.rowVersion,
        defaultModelId: next.defaultModelId ?? null,
        overrideAllChatModels: next.overrideAllChatModels,
        temperature: next.temperature ?? null,
        topP: next.topP ?? null,
        reasoningEffort: next.reasoningEffort ?? null,
        samplingParametersJson: next.samplingParametersJson ?? null,
      });
      setChatDefaults(updated);
      setChatDefaultsError(null);
      await onRefresh();
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
      <div className={`text-sm ${statusToneClass(chat.status)}`}>{chat.summary}</div>
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
          .map((option) => (
            <button
              key={option.modelId}
              type="button"
              className={`${textButtonClassName('neutral')} w-full justify-start text-left ${overrideAllChatModels ? '' : 'cursor-default'}`}
              role="option"
              aria-selected={option.modelId === currentModelId}
              disabled={!overrideAllChatModels}
              onClick={() => {
                void setGlobalModel(option.modelId);
              }}
            >
              {option.displayName} <span className="text-slate-500">({option.provider})</span>
              {option.modelId === currentModelId ? ' (current)' : ''}
            </button>
          ))}
      </div>

      {chat.supportsLocalRuntimePower && (
        <div className="mt-2 border-t pt-2 flex items-center gap-2">
          <span className="text-xs text-slate-700">Local runtime</span>
          {hasPendingOp ? <FaSpinner className="w-3.5 h-3.5 animate-spin text-blue-600" /> : null}
          <button
            type="button"
            className="p-1.5 rounded border border-emerald-300 text-emerald-700"
            aria-label="Turn local chat runtime on"
            title="On"
            onClick={() => void powerOn()}
          >
            <FaPlay className="w-3.5 h-3.5" />
          </button>
          <button
            type="button"
            className="p-1.5 rounded border border-slate-300 text-slate-700"
            aria-label="Turn local chat runtime off"
            title="Off"
            onClick={onRequestUnloadConfirm}
          >
            <FaStop className="w-3.5 h-3.5" />
          </button>
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
