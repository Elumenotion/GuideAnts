import React from 'react';
import { render as rtlRender, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import DraftUserCell from '../DraftUserCell';
import { ToastProvider } from '../../../common/Toast';

const render = (ui: React.ReactElement) => rtlRender(ui, { wrapper: ToastProvider });

vi.mock('../../../../contexts/NotebookContext', () => ({
  __esModule: true,
  useNotebook: () => ({ notebook: { id: 'nb-1', projectId: 'proj-1' } }),
}));

const mockUseConversation = vi.fn();
vi.mock('../../../../contexts/ConversationContext', () => ({
  __esModule: true,
  useConversation: () => mockUseConversation(),
}));

vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: { uploadFiles: vi.fn().mockResolvedValue([]) },
}));

vi.mock('../../../../services/api', () => ({
  api: { projects: { notebooks: { conversations: { compact: vi.fn() } } } },
}));

describe('DraftUserCell context meter and compact button', () => {
  it('renders the context meter and compact button using context data', () => {
    mockUseConversation.mockReturnValue({
      pendingAttachments: [],
      addPendingAttachment: vi.fn(),
      removePendingAttachment: vi.fn(),
      conversationId: 'c1',
      contextStatus: {
        contextWindowTokens: 8192,
        estimatedPromptTokens: 4096,
        boundaryTurnIndex: null,
        estimateSource: 'ProviderUsage',
        modelDeploymentId: 'gpt-4o-mini',
        contextWindowSource: 'Catalog',
      },
      refresh: vi.fn(),
    });

    render(<DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} />);

    expect(screen.getByTestId('context-meter')).toHaveTextContent('50%');
    expect(screen.getByRole('button', { name: /compact/i })).toBeInTheDocument();
  });

  it('disables the compact button while streaming (busy)', () => {
    mockUseConversation.mockReturnValue({
      pendingAttachments: [],
      addPendingAttachment: vi.fn(),
      removePendingAttachment: vi.fn(),
      conversationId: 'c1',
      contextStatus: null,
      refresh: vi.fn(),
    });

    render(<DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} isStreaming />);

    expect(screen.getByRole('button', { name: /compact/i })).toBeDisabled();
  });
});
