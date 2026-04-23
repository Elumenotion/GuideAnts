import type {
  NotebookHeaderToolbarDto,
  NotebookToolbarChatDto,
  NotebookToolbarServiceDto,
} from '../../../types/notebookToolbar';

export type ToolbarPanelId = 'chat' | 'image' | 'tts' | 'asr' | null;

export interface ToolbarMutationContext {
  projectId: string;
  notebookId: string;
  conversationId: string | null;
  inFlight: boolean;
  setInFlight: (value: boolean) => void;
  onRefresh: () => Promise<void>;
}

export interface NotebookServiceToolbarProps extends ToolbarMutationContext {
  data: NotebookHeaderToolbarDto | null;
  isLoading: boolean;
  isMobile: boolean;
  assistantByName: Record<string, { id?: string } | undefined>;
  onMobileOpen?: () => void;
}

export interface ServicePanelCommonProps extends ToolbarMutationContext {
  service: NotebookToolbarServiceDto;
  onOpenSettings: () => void;
  showWorkspaceCopy?: boolean;
}

export interface ChatPanelProps extends ToolbarMutationContext {
  chat: NotebookToolbarChatDto;
  assistantIdForLlama?: string;
  onOpenSettings: () => void;
  onRequestUnloadConfirm: () => void;
  showWorkspaceCopy?: boolean;
}
