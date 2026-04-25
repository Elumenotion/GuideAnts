import { useEffect, useRef, useState, type ReactElement } from 'react';
import { useNavigate } from 'react-router-dom';
import { FaCommentDots, FaEye, FaSpinner, FaSyncAlt } from 'react-icons/fa';
import { GiHumanEar, GiLips } from 'react-icons/gi';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';
import { NotebookServiceButton } from './NotebookServiceButton';
import { NotebookServicePopover } from './NotebookServicePopover';
import { NotebookServiceSheet } from './NotebookServiceSheet';
import { ChatToolbarPanel } from './ChatToolbarPanel';
import { ImageToolbarPanel } from './ImageToolbarPanel';
import { TtsToolbarPanel } from './TtsToolbarPanel';
import { AsrToolbarPanel } from './AsrToolbarPanel';
import type { NotebookServiceToolbarProps, ToolbarPanelId } from './types';
import { api } from '../../../services/api';
import {
  statusDotClass,
  toolbarRefreshButtonClass,
  toolbarServiceButtonClass,
  toolbarServiceIconHeaderClass,
  toolbarServiceStatusDotBorderClass,
  withToolbarServiceIcon,
  type ToolbarServiceColorKey,
} from './toolbarFormatters';

const SETTINGS_PATH = '/settings';

const toolbarServiceIcons: Record<ToolbarServiceColorKey, ReactElement> = {
  chat: <FaCommentDots />,
  image: <FaEye />,
  tts: <GiLips />,
  asr: <GiHumanEar />,
};

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
  const [sheetFocus, setSheetFocus] = useState<Exclude<ToolbarPanelId, null> | null>(null);
  const [confirmUnloadChat, setConfirmUnloadChat] = useState(false);
  const sectionRefs = useRef<Partial<Record<Exclude<ToolbarPanelId, null>, HTMLElement | null>>>({});

  useEffect(() => {
    if (!sheetOpen || !sheetFocus) return;
    const el = sectionRefs.current[sheetFocus];
    if (el) {
      el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }
  }, [sheetOpen, sheetFocus]);

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

  const order: Array<{
    id: ToolbarServiceColorKey;
    label: string;
    status: string;
  }> = [
    { id: 'chat', label: 'Chat', status: data.chat.status },
    {
      id: 'image',
      label: 'Image generation',
      status: data.services.find((service) => service.kind === 'image')?.status ?? 'blocked',
    },
    {
      id: 'tts',
      label: 'Speech synthesis (TTS)',
      status: data.services.find((service) => service.kind === 'tts')?.status ?? 'blocked',
    },
    {
      id: 'asr',
      label: 'Speech transcription (ASR)',
      status: data.services.find((service) => service.kind === 'asr')?.status ?? 'blocked',
    },
  ];

  const openMobileSheet = (initial: Exclude<ToolbarPanelId, null> | null) => {
    setSheetFocus(initial);
    setSheetOpen(true);
    onMobileOpen?.();
  };

  if (isMobile) {
    return (
      <div ref={rootRef} className="relative w-full max-w-full min-w-0">
        <div
          className="flex w-full min-w-0 max-w-full flex-nowrap items-center justify-start gap-0.5 overflow-x-auto overflow-y-hidden py-0.5 [scrollbar-width:thin] md:justify-center"
          data-testid="notebook-service-toolbar-mobile"
        >
          {order.map((entry) => (
            <div key={entry.id} className="relative flex shrink-0">
              <button
                type="button"
                className={toolbarServiceButtonClass(entry.id, { expanded: false, minSize: 'sm' })}
                aria-label={entry.label}
                title={entry.label}
                onClick={() => {
                  setOpenPanel(null);
                  openMobileSheet(entry.id);
                }}
              >
                <span
                  className={`absolute top-0.5 right-0.5 h-1.5 w-1.5 rounded-full border ${toolbarServiceStatusDotBorderClass(
                    entry.id
                  )} ${statusDotClass(entry.status)}`}
                  aria-hidden
                />
                {withToolbarServiceIcon(entry.id, toolbarServiceIcons[entry.id])}
              </button>
            </div>
          ))}
          <button
            type="button"
            className={toolbarRefreshButtonClass(true)}
            title="Refresh toolbar"
            aria-label="Refresh toolbar"
            disabled={inFlight}
            onClick={() => {
              void onRefresh();
            }}
          >
            {inFlight ? <FaSpinner className="h-4 w-4 shrink-0 animate-spin text-slate-600" /> : <FaSyncAlt className="h-4 w-4 shrink-0 text-slate-600" />}
          </button>
        </div>
        <NotebookServiceSheet
          open={sheetOpen}
          onClose={() => {
            setSheetOpen(false);
            setSheetFocus(null);
          }}
        >
          <div className="space-y-5">
            {order.map((entry) => (
              <section
                key={entry.id}
                id={`toolbar-section-${entry.id}`}
                ref={(node) => {
                  sectionRefs.current[entry.id] = node;
                }}
              >
                <h3 className="text-sm font-semibold mb-2 flex items-center gap-2">
                  <span className={toolbarServiceIconHeaderClass(entry.id)} aria-hidden>
                    {withToolbarServiceIcon(entry.id, toolbarServiceIcons[entry.id])}
                  </span>
                  {entry.label}
                </h3>
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
    <div
      ref={rootRef}
      className="flex w-full min-w-0 max-w-full flex-nowrap items-center justify-center gap-0.5 sm:gap-1"
      data-testid="notebook-service-toolbar"
    >
      {order.map((entry) => (
        <div key={entry.id} className="relative flex shrink-0">
          <NotebookServiceButton
            serviceId={entry.id}
            label={entry.label}
            icon={toolbarServiceIcons[entry.id]}
            status={entry.status}
            expanded={openPanel === entry.id}
            onClick={() => setOpenPanel(openPanel === entry.id ? null : entry.id)}
          />
          <NotebookServicePopover open={openPanel === entry.id}>
            {renderPanel(entry.id, true)}
          </NotebookServicePopover>
        </div>
      ))}
      <button
        type="button"
        className={toolbarRefreshButtonClass(false)}
        title="Refresh toolbar"
        aria-label="Refresh toolbar"
        disabled={inFlight}
        onClick={() => {
          void onRefresh();
        }}
      >
        {inFlight ? <FaSpinner className="h-3.5 w-3.5 shrink-0 animate-spin text-slate-600" /> : <FaSyncAlt className="h-3.5 w-3.5 shrink-0 text-slate-600" />}
      </button>
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
