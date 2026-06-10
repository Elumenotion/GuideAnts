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

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getAllByText('Document preview unavailable').length).toBeGreaterThan(0);
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

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByTestId('documentserver-inline-error')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /extracted text/i })).toBeInTheDocument();
  });

  it('clears loading state when onDocumentReady fires', async () => {
    render(
      <DocumentServerEditor
        scope="project"
        projectId="project-1"
        fileId="file-1"
        canEdit
      />,
    );

    await waitFor(() => {
      expect(runtimeConfig?.events?.onDocumentReady).toEqual(expect.any(Function));
    });

    act(() => {
      runtimeConfig!.events.onDocumentReady({});
    });

    expect(screen.queryByText('Loading DocumentServer editor...')).not.toBeInTheDocument();
  });

  it('reports mount failures from config fetch', async () => {
    createDocumentServerEditorConfigMock.mockRejectedValueOnce(new Error('Config unavailable'));

    render(
      <DocumentServerEditor
        scope="project"
        projectId="project-1"
        fileId="file-1"
        canEdit
      />,
    );

    expect(await screen.findByTestId('documentserver-inline-error')).toBeInTheDocument();
    expect(screen.getAllByText('Config unavailable').length).toBeGreaterThan(0);
  });

  it('retries editor setup when retry is clicked', async () => {
    createDocumentServerEditorConfigMock.mockRejectedValueOnce(new Error('Temporary failure'));

    render(
      <DocumentServerEditor
        scope="project"
        projectId="project-1"
        fileId="file-1"
        canEdit
        showErrorDialogOnError={false}
      />,
    );

    expect(await screen.findByText('Temporary failure')).toBeInTheDocument();
    const initialCalls = createDocumentServerEditorConfigMock.mock.calls.length;

    act(() => {
      screen.getByRole('button', { name: 'Retry' }).click();
    });

    await waitFor(() => {
      expect(createDocumentServerEditorConfigMock.mock.calls.length).toBeGreaterThan(initialCalls);
    });
  });

  it('loads the client script when it is not already present', async () => {
    document.head.querySelectorAll('script[data-documentserver]').forEach(script => script.remove());
    delete (window as any).DocsAPI;

    const scriptUrl = 'http://localhost:8082/web-apps/apps/api/documents/api.js';
    createDocumentServerEditorConfigMock.mockResolvedValueOnce({
      documentServerUrl: 'http://localhost:8082',
      config: {
        document: { title: 'proposal.docx' },
        editorConfig: { mode: 'view' },
      },
    });

    render(
      <DocumentServerEditor
        scope="project"
        projectId="project-1"
        fileId="file-1"
        canEdit={false}
      />,
    );

    await waitFor(() => {
      expect(document.querySelector(`script[data-documentserver="${scriptUrl}"]`)).not.toBeNull();
    });

    const script = document.querySelector(`script[data-documentserver="${scriptUrl}"]`) as HTMLScriptElement;

    act(() => {
      (window as any).DocsAPI = { DocEditor: docEditorMock };
      script.dispatchEvent(new Event('load'));
    });

    await waitFor(() => {
      expect(docEditorMock).toHaveBeenCalled();
    });
  });

  it('invokes onError callback and handles onWarning events', async () => {
    const onError = vi.fn();

    render(
      <DocumentServerEditor
        scope="notebook"
        projectId="project-1"
        notebookId="notebook-1"
        fileId="file-1"
        canEdit
        onError={onError}
        showErrorDialogOnError={false}
      />,
    );

    await waitFor(() => {
      expect(runtimeConfig?.events?.onWarning).toEqual(expect.any(Function));
    });

    act(() => {
      runtimeConfig!.events.onWarning({ message: 'Minor warning' });
      runtimeConfig!.events.onError({ data: { errorCode: 1, errorDescription: 'Load failed.' } });
    });

    expect(onError).toHaveBeenCalledWith(expect.stringContaining('Load failed.'));
    expect(screen.getByText(/DocumentServer runtime error \(1\): Load failed\./)).toBeInTheDocument();
  });

  it('destroys the editor instance on unmount', async () => {
    const { unmount } = render(
      <DocumentServerEditor
        scope="project"
        projectId="project-1"
        fileId="file-1"
        canEdit
      />,
    );

    await waitFor(() => {
      expect(docEditorMock).toHaveBeenCalled();
    });

    unmount();
    expect(destroyEditor).toHaveBeenCalled();
  });
});
