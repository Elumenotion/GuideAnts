import React from 'react';
import { render as rtlRender, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import DraftUserCell from '../DraftUserCell';
import { ToastProvider } from '../../../common/Toast';
import {
  getConversationReasoningEffortOverride,
  setConversationReasoningEffortOverride,
} from '../../../../contexts/conversation/conversationReasoningEffortOverride';

// Configurable context mocks
let notebookMock: Record<string, unknown> = {};
let conversationMock: Record<string, unknown> = {};

vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => notebookMock,
}));

vi.mock('../../../../contexts/ConversationContext', () => ({
  useConversation: () => conversationMock,
}));

vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: { uploadFiles: vi.fn().mockResolvedValue([]) },
}));

vi.mock('../../../../hooks/useAudioRecorder', () => ({
  useAudioRecorder: () => ({
    isRecording: false,
    isProcessing: false,
    duration: 0,
    isSupported: false,
    startRecording: vi.fn(),
    stopRecording: vi.fn(),
  }),
}));

vi.mock('../../../common/CameraCapture', () => ({
  default: () => null,
}));

const CONVO = 'convo-thinking-test';

const render = (ui: React.ReactElement) =>
  rtlRender(ui, { wrapper: ToastProvider });

// Fire a key on the editor container (the div carrying onKeyDownCapture)
function fireKeyOnEditor(key: string, extra: Record<string, unknown> = {}) {
  const group = screen.getByRole('group', { name: 'Compose message' });
  const container = group.querySelector('.relative') ?? group;
  fireEvent.keyDown(container, { key, ...extra });
}

const baseProps = { value: '', onChange: vi.fn(), onSend: vi.fn(), canEdit: true, isStreaming: false };

beforeEach(() => {
  cleanup();
  notebookMock = {
    notebook: { id: 'nb-1', projectId: 'proj-1' },
    chatReadiness: { effectiveModelId: 'model-think-1' },
    catalogModels: [
      { modelId: 'model-think-1', displayName: 'Thinky', isActive: true, reasoningChoices: ['Low', 'Medium', 'High'] },
    ],
  };
  conversationMock = {
    conversationId: CONVO,
    pendingAttachments: [],
    addPendingAttachment: vi.fn(),
    removePendingAttachment: vi.fn(),
  };
  setConversationReasoningEffortOverride(CONVO, null);
});

describe('DraftUserCell – thinking-level override', () => {
  it('opens the picker on Ctrl+` when the model has choices', () => {
    render(<DraftUserCell {...baseProps} />);
    expect(screen.queryByText('Thinking Level')).not.toBeInTheDocument();
    fireKeyOnEditor('`', { ctrlKey: true });
    expect(screen.getByText('Thinking Level')).toBeInTheDocument();
    expect(screen.getByText('High')).toBeInTheDocument();
  });

  it('is a no-op on Ctrl+` when the model has no choices', () => {
    notebookMock = {
      notebook: { id: 'nb-1', projectId: 'proj-1' },
      chatReadiness: { effectiveModelId: 'model-nothink' },
      catalogModels: [{ modelId: 'model-nothink', displayName: 'Plain', isActive: true }],
    };
    render(<DraftUserCell {...baseProps} />);
    fireKeyOnEditor('`', { ctrlKey: true });
    expect(screen.queryByText('Thinking Level')).not.toBeInTheDocument();
  });

  it('is a no-op on Ctrl+` when read-only', () => {
    render(<DraftUserCell {...baseProps} canEdit={false} />);
    fireKeyOnEditor('`', { ctrlKey: true });
    expect(screen.queryByText('Thinking Level')).not.toBeInTheDocument();
  });

  it('selecting a choice sets the chip and the registry', () => {
    render(<DraftUserCell {...baseProps} />);
    fireKeyOnEditor('`', { ctrlKey: true });
    fireEvent.click(screen.getByText('High'));
    expect(screen.getByText('Thinking: High')).toBeInTheDocument();
    expect(getConversationReasoningEffortOverride(CONVO)).toBe('High');
  });

  it('clicking a picker option with the mouse selects it (not just keyboard)', () => {
    render(<DraftUserCell {...baseProps} />);
    fireKeyOnEditor('`', { ctrlKey: true });
    // mousedown + click on the option (this is what the mouse does; previously the
    // document mousedown closed the picker before onClick could fire).
    const option = screen.getByText('Medium');
    fireEvent.mouseDown(option);
    fireEvent.click(option);
    expect(screen.getByText('Thinking: Medium')).toBeInTheDocument();
    expect(getConversationReasoningEffortOverride(CONVO)).toBe('Medium');
  });

  it('the × on the chip clears both the chip and the registry', () => {
    render(<DraftUserCell {...baseProps} />);
    fireKeyOnEditor('`', { ctrlKey: true });
    fireEvent.click(screen.getByText('High'));
    expect(getConversationReasoningEffortOverride(CONVO)).toBe('High');
    fireEvent.click(screen.getByLabelText('Clear thinking level'));
    expect(screen.queryByText('Thinking: High')).not.toBeInTheDocument();
    expect(getConversationReasoningEffortOverride(CONVO)).toBeNull();
  });

  it('closes the picker on Escape', () => {
    render(<DraftUserCell {...baseProps} />);
    fireKeyOnEditor('`', { ctrlKey: true });
    expect(screen.getByText('Thinking Level')).toBeInTheDocument();
    fireKeyOnEditor('Escape');
    expect(screen.queryByText('Thinking Level')).not.toBeInTheDocument();
  });

  it('shows the chip on mount when the registry is pre-populated (soft nav)', () => {
    setConversationReasoningEffortOverride(CONVO, 'Medium');
    render(<DraftUserCell {...baseProps} />);
    expect(screen.getByText('Thinking: Medium')).toBeInTheDocument();
  });

  it('does not show the chip on a fresh mount (empty registry)', () => {
    render(<DraftUserCell {...baseProps} />);
    expect(screen.queryByText(/Thinking:/)).not.toBeInTheDocument();
  });

  it('clears the registry on unmount (navigate away)', () => {
    setConversationReasoningEffortOverride(CONVO, 'High');
    const { unmount } = render(<DraftUserCell {...baseProps} />);
    expect(getConversationReasoningEffortOverride(CONVO)).toBe('High');
    unmount();
    expect(getConversationReasoningEffortOverride(CONVO)).toBeNull();
  });

  it('auto-clears when the effective model changes to one that lacks the choice', () => {
    setConversationReasoningEffortOverride(CONVO, 'High');
    const { rerender } = render(<DraftUserCell {...baseProps} />);
    expect(screen.getByText('Thinking: High')).toBeInTheDocument();
    // Model changes to one whose choices exclude "High"
    notebookMock = {
      notebook: { id: 'nb-1', projectId: 'proj-1' },
      chatReadiness: { effectiveModelId: 'model-low-only' },
      catalogModels: [
        { modelId: 'model-low-only', displayName: 'LowOnly', isActive: true, reasoningChoices: ['Low'] },
      ],
    };
    rerender(<DraftUserCell {...baseProps} />);
    expect(screen.queryByText('Thinking: High')).not.toBeInTheDocument();
    expect(getConversationReasoningEffortOverride(CONVO)).toBeNull();
  });

  it('shows the "Ctrl+` to set thinking" hint when the model supports thinking', () => {
    render(<DraftUserCell {...baseProps} />);
    expect(screen.getByText(/to set thinking/)).toBeInTheDocument();
  });

  it('hides the thinking hint when the model has no choices', () => {
    notebookMock = {
      notebook: { id: 'nb-1', projectId: 'proj-1' },
      chatReadiness: { effectiveModelId: 'model-nothink' },
      catalogModels: [{ modelId: 'model-nothink', displayName: 'Plain', isActive: true }],
    };
    render(<DraftUserCell {...baseProps} />);
    expect(screen.queryByText(/to set thinking/)).not.toBeInTheDocument();
  });

  it('does NOT auto-clear while the catalog is still empty (not loaded)', () => {
    setConversationReasoningEffortOverride(CONVO, 'High');
    const { rerender } = render(<DraftUserCell {...baseProps} />);
    expect(screen.getByText('Thinking: High')).toBeInTheDocument();
    // Model changes but catalog not yet loaded
    notebookMock = {
      notebook: { id: 'nb-1', projectId: 'proj-1' },
      chatReadiness: { effectiveModelId: 'model-other' },
      catalogModels: [],
    };
    rerender(<DraftUserCell {...baseProps} />);
    expect(screen.getByText('Thinking: High')).toBeInTheDocument();
    expect(getConversationReasoningEffortOverride(CONVO)).toBe('High');
  });
});
