import React from 'react';
import { act, render, screen, waitFor } from '../../../test/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import DocumentServerEditor from '../DocumentServerEditor';
import { createDocumentServerEditorConfig } from '../../../services/documentServer';

vi.mock('../../../services/documentServer', () => ({
  createDocumentServerEditorConfig: vi.fn(),
}));

describe('DocumentServerEditor', () => {
  const createDocumentServerEditorConfigMock = vi.mocked(createDocumentServerEditorConfig);
  let runtimeConfig: Record<string, any> | null = null;
  let destroyEditor: ReturnType<typeof vi.fn>;
  let docEditorMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    runtimeConfig = null;
    destroyEditor = vi.fn();
    document.head.querySelectorAll('script[data-documentserver]').forEach(script => script.remove());
    delete (window as any).DocsAPI;

    createDocumentServerEditorConfigMock.mockResolvedValue({
      documentServerUrl: 'http://localhost:8082',
      config: {
        documentType: 'word',
        type: 'desktop',
        document: {
          title: 'proposal.docx',
          fileType: 'docx',
          key: 'key',
          url: 'http://api.local/download',
        },
        editorConfig: {
          callbackUrl: 'http://api.local/callback',
          mode: 'edit',
        },
      },
    });

    const script = document.createElement('script');
    script.dataset.documentserver = 'http://localhost:8082/web-apps/apps/api/documents/api.js';
    script.dataset.loaded = 'true';
    document.head.appendChild(script);

    docEditorMock = vi.fn(function (_elementId: string, config: Record<string, any>) {
        runtimeConfig = config;
        this.destroyEditor = destroyEditor;
    });

    (window as any).DocsAPI = {
      DocEditor: docEditorMock,
    };
  });

  it('shows runtime errors in the standard dialog surface instead of a full editor overlay', async () => {
    render(
      <DocumentServerEditor
        scope="project"
        projectId="project-1"
        fileId="file-1"
        canEdit
      />
    );

    await waitFor(() => {
      expect(docEditorMock).toHaveBeenCalled();
      expect(runtimeConfig?.events?.onError).toEqual(expect.any(Function));
    });

    act(() => {
      runtimeConfig!.events.onError({
        data: {
          errorCode: -4,
          errorDescription: 'Download failed.',
        },
      });
    });

    expect(screen.getByRole('dialog', { name: /document preview unavailable/i })).toBeInTheDocument();
    expect(screen.getByTestId('documentserver-error-dialog-backdrop')).toHaveClass('z-[10010]');
    expect(screen.getByTestId('documentserver-inline-error')).toBeInTheDocument();
    expect(screen.getAllByText(/DocumentServer runtime error \(-4\): Download failed\./)).toHaveLength(2);
    expect(document.querySelector('.absolute.inset-0.text-red-600')).not.toBeInTheDocument();
  });

  it('can keep runtime errors inline so parent tab controls remain usable', async () => {
    render(
      <div>
        <div role="tablist" aria-label="Preview tabs">
          <button role="tab">Original Content</button>
          <button role="tab">Extracted Text</button>
        </div>
        <DocumentServerEditor
          scope="notebook"
          projectId="project-1"
          notebookId="notebook-1"
          fileId="file-1"
          canEdit
          showErrorDialogOnError={false}
        />
      </div>
    );

    await waitFor(() => {
      expect(docEditorMock).toHaveBeenCalled();
      expect(runtimeConfig?.events?.onError).toEqual(expect.any(Function));
    });

    act(() => {
      runtimeConfig!.events.onError({
        data: {
          errorCode: -4,
          errorDescription: 'Download failed.',
        },
      });
    });

    expect(screen.queryByRole('dialog', { name: /document preview unavailable/i })).not.toBeInTheDocument();
    expect(screen.getByTestId('documentserver-inline-error')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /extracted text/i })).toBeInTheDocument();
  });
});
