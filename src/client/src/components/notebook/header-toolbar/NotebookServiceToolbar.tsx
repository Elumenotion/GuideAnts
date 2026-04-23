import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { FaSyncAlt, FaSpinner } from 'react-icons/fa';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';
import { textButtonClassName } from '../../../pages/settings/components/shared/ActionButtons';
import { NotebookServiceButton } from './NotebookServiceButton';
import { NotebookServicePopover } from './NotebookServicePopover';
import { NotebookServiceSheet } from './NotebookServiceSheet';
import { ChatToolbarPanel } from './ChatToolbarPanel';
import { ImageToolbarPanel } from './ImageToolbarPanel';
import { TtsToolbarPanel } from './TtsToolbarPanel';
import { AsrToolbarPanel } from './AsrToolbarPanel';
import type { NotebookServiceToolbarProps, ToolbarPanelId } from './types';
import { api } from '../../../services/api';

const SETTINGS_PATH = '/settings';

export function NotebookServiceToolbar({
  projectId,
  notebookId,
  conversationId,
  data,
  isLoading,
  isMobile,
  onRefresh,
  inFlight,
  setInFlight,
  assistantByName,
  onMobileOpen,
}: NotebookServiceToolbarProps) {
  const navigate = useNavigate();
  const rootRef = useRef<HTMLDivElement>(null);
  const [openPanel, setOpenPanel] = useState<ToolbarPanelId>(null);
  const [sheetOpen, setSheetOpen] = useState(false);
  const [confirmUnloadChat, setConfirmUnloadChat] = useState(false);

  useEffect(() => {
    const onDocDown = (event: MouseEvent) => {
      if (!rootRef.current) return;
      if (!rootRef.current.contains(event.target as Node)) {
        setOpenPanel(null);
      }
    };
    document.addEventListener('mousedown', onDocDown);
    return () => document.removeEventListener('mousedown', onDocDown);
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      setOpenPanel(null);
      setSheetOpen(false);
      setConfirmUnloadChat(false);
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, []);

  const assistantIdForLlama = data?.chat.selectedAssistantName
    ? assistantByName[data.chat.selectedAssistantName]?.id
    : undefined;

  const confirmChatUnload = async () => {
    setConfirmUnloadChat(false);
    setInFlight(true);
    try {
      await api.projects.notebooks.conversations.unloadLlamaRuntime(
        projectId,
        notebookId,
        assistantIdForLlama
      );
      await onRefresh();
    } finally {
      setInFlight(false);
    }
  };

  const openSettings = () => {
    navigate(SETTINGS_PATH);
    setOpenPanel(null);
    setSheetOpen(false);
  };

  const renderPanel = (panel: Exclude<ToolbarPanelId, null>, showWorkspaceCopy = true) => {
    if (!data) return null;
    if (panel === 'chat') {
      return (
        <ChatToolbarPanel
          chat={data.chat}
          projectId={projectId}
          notebookId={notebookId}
          conversationId={conversationId}
          inFlight={inFlight}
          setInFlight={setInFlight}
          onRefresh={onRefresh}
          assistantIdForLlama={assistantIdForLlama}
          onOpenSettings={openSettings}
          onRequestUnloadConfirm={() => setConfirmUnloadChat(true)}
          showWorkspaceCopy={showWorkspaceCopy}
        />
      );
    }

    const service = data.services.find((item) => item.kind === panel);
    if (!service) return null;

    const commonProps = {
      service,
      projectId,
      notebookId,
      conversationId,
      inFlight,
      setInFlight,
      onRefresh,
      onOpenSettings: openSettings,
      showWorkspaceCopy,
    };

    if (panel === 'image') return <ImageToolbarPanel {...commonProps} />;
    if (panel === 'tts') return <TtsToolbarPanel {...commonProps} />;
    return <AsrToolbarPanel {...commonProps} />;
  };

  if (isLoading && !data) {
    return <div className="text-xs text-slate-500 px-1">Loading...</div>;
  }
  if (!data) return null;

  const order: Array<{ id: Exclude<ToolbarPanelId, null>; label: string; status: string }> = [
    { id: 'chat', label: 'Chat', status: data.chat.status },
    {
      id: 'image',
      label: 'Image Generation',
      status: data.services.find((service) => service.kind === 'image')?.status ?? 'blocked',
    },
    {
      id: 'tts',
      label: 'Speech Synthesis',
      status: data.services.find((service) => service.kind === 'tts')?.status ?? 'blocked',
    },
    {
      id: 'asr',
      label: 'Speech Transcription',
      status: data.services.find((service) => service.kind === 'asr')?.status ?? 'blocked',
    },
  ];

  if (isMobile) {
    return (
      <div ref={rootRef} className="relative">
        <button
          type="button"
          className={`${textButtonClassName('neutral')} min-h-10 text-sm`}
          onClick={() =>
            setSheetOpen((open) => {
              const next = !open;
              if (next) {
                onMobileOpen?.();
              }
              return next;
            })
          }
        >
          Services
        </button>
        <NotebookServiceSheet open={sheetOpen} onClose={() => setSheetOpen(false)}>
          <div className="space-y-5">
            {order.map((entry) => (
              <section key={entry.id}>
                <h3 className="text-sm font-semibold mb-2">{entry.label}</h3>
                {renderPanel(entry.id, false)}
              </section>
            ))}
          </div>
        </NotebookServiceSheet>
        <ConfirmationDialog
          isOpen={confirmUnloadChat}
          onClose={() => setConfirmUnloadChat(false)}
          onConfirm={() => {
            void confirmChatUnload();
          }}
          title="Turn off local chat runtime?"
          message="Stops the llama runtime for the workspace and frees memory."
          confirmText="Turn off"
        />
      </div>
    );
  }

  return (
    <div ref={rootRef} className="flex items-center gap-1 min-w-0">
      <span className="hidden lg:block text-[10px] text-slate-500 max-w-[5rem] leading-tight">
        Workspace controls
      </span>
      <button
        type="button"
        className="p-1.5 rounded border border-slate-200 text-slate-500 hover:bg-slate-50 focus-visible:ring-2 focus-visible:ring-blue-500 flex-shrink-0 relative z-10"
        title="Refresh toolbar"
        aria-label="Refresh toolbar"
        disabled={inFlight}
        onClick={() => {
          void onRefresh();
        }}
      >
        {inFlight ? <FaSpinner className="w-3.5 h-3.5 animate-spin" /> : <FaSyncAlt className="w-3.5 h-3.5" />}
      </button>
      {order.map((entry) => (
        <div key={entry.id} className="relative flex-shrink-0">
          <NotebookServiceButton
            label={entry.label}
            status={entry.status}
            expanded={openPanel === entry.id}
            onClick={() => setOpenPanel(openPanel === entry.id ? null : entry.id)}
          />
          <NotebookServicePopover open={openPanel === entry.id}>
            {renderPanel(entry.id, true)}
          </NotebookServicePopover>
        </div>
      ))}
      <ConfirmationDialog
        isOpen={confirmUnloadChat}
        onClose={() => setConfirmUnloadChat(false)}
        onConfirm={() => {
          void confirmChatUnload();
        }}
        title="Turn off local chat runtime?"
        message="Stops the llama runtime for the workspace and frees memory."
        confirmText="Turn off"
      />
    </div>
  );
}
