import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../../test/test-utils';
import MarkdownViewer from '../MarkdownViewer';
import '@testing-library/jest-dom';
import { waitFor } from '@testing-library/react';
import { api } from '@/services/api';

// Mock for mermaid so dynamic import resolves during tests
vi.mock('mermaid', () => ({
  default: {
    initialize: vi.fn(),
    parse: vi.fn().mockResolvedValue(true),
    render: vi.fn().mockResolvedValue({ svg: '<svg data-testid="mermaid-svg"></svg>' }),
  },
}));

vi.mock('@/services/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/services/api')>();
  return {
    api: {
      ...actual.api,
      utils: {
        ...actual.api.utils,
        getAuthenticatedUrl: vi.fn(),
      },
    },
  };
});

const getAuthenticatedUrl = vi.mocked(api.utils.getAuthenticatedUrl);

const InlinePngDataUrl =
  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6Qx0sAAAAASUVORK5CYII=';

describe('MarkdownViewer', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:mock-image',
      fileName: 'image.png',
    });
  });

  it('renders headings and paragraphs correctly', () => {
    const md = '# Hello World\n\nThis is **markdown**.';
    render(<MarkdownViewer text={md} />);

    const heading = screen.getByRole('heading', { level: 1 });
    expect(heading).toHaveTextContent('Hello World');
    expect(screen.getByText('markdown')).toBeInTheDocument();
  });

  it('renders lists', () => {
    const md = '- item 1\n- item 2';
    render(<MarkdownViewer text={md} />);

    const items = screen.getAllByRole('listitem');
    expect(items).toHaveLength(2);
  });

  it('renders blockquote and inline code', () => {
    const md = '> quote\n\nHere is `code`.';
    render(<MarkdownViewer text={md} />);
    const quote = screen.getByText('quote').closest('blockquote');
    expect(quote).toBeInTheDocument();
    expect(screen.getByText('code')).toBeInTheDocument();
  });

  it('renders within a PreviewContainer', () => {
    render(<MarkdownViewer text="some text" />);
    expect(screen.getByText('Preview')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Full screen' })).toBeInTheDocument();
  });

  it('handles in-document #anchor links without opening new windows', async () => {
    const md = '# Getting Started\n\nSee [Getting Started](#getting-started).';
    const { container } = render(<MarkdownViewer text={md} />);

    const heading = screen.getByRole('heading', { level: 1 });
    // rehype-slug should assign id "getting-started"
    expect(heading).toHaveAttribute('id', 'getting-started');

    const link = screen.getByRole('link', { name: 'Getting Started' });

    const scrollSpy = vi.spyOn(Element.prototype as any, 'scrollIntoView').mockImplementation(() => {});
    const openSpy = vi.spyOn(window, 'open');

    link.dispatchEvent(new MouseEvent('click', { bubbles: true }));

    await waitFor(() => {
      expect(scrollSpy).toHaveBeenCalled();
    });
    expect(openSpy).not.toHaveBeenCalled();

    scrollSpy.mockRestore();
    openSpy.mockRestore();
  });

  it('renders inline base64 image data URLs', () => {
    render(<MarkdownViewer text={`![tiny](${InlinePngDataUrl})`} inlineMode={true} />);

    const img = screen.getByRole('img', { name: 'tiny' }) as HTMLImageElement;
    expect(img).toBeInTheDocument();
    expect(img.getAttribute('src')).toContain('data:image/png;base64,');
  });

  it('keeps non-image data URLs sanitized', () => {
    const dataTextUrl = 'data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==';
    render(<MarkdownViewer text={`[unsafe-link](${dataTextUrl})`} inlineMode={true} />);

    const link = screen.getByText('unsafe-link').closest('a') as HTMLAnchorElement | null;
    expect(link).not.toBeNull();
    const href = link?.getAttribute('href') || '';
    expect(href).not.toContain('data:text/html');
  });

  it('renders inline mode without PreviewContainer chrome', () => {
    render(<MarkdownViewer text="Inline body" inlineMode />);
    expect(screen.queryByText('Preview')).not.toBeInTheDocument();
    expect(screen.getByText('Inline body')).toBeInTheDocument();
  });

  it('hides the preview header when hidePreviewHeader is set', () => {
    render(<MarkdownViewer text="Hidden header" hidePreviewHeader />);
    expect(screen.queryByText('Preview')).not.toBeInTheDocument();
    expect(screen.getByText('Hidden header')).toBeInTheDocument();
  });

  it('renders full HTML documents inside an iframe', () => {
    const html = '<!DOCTYPE html><html><body><h1>Embedded</h1></body></html>';
    render(<MarkdownViewer text={html} inlineMode />);
    const iframe = screen.getByTitle('HTML Content') as HTMLIFrameElement;
    expect(iframe).toBeInTheDocument();
    expect(iframe.getAttribute('srcdoc')).toContain('<h1>Embedded</h1>');
  });

  it('renders mermaid code blocks', async () => {
    const md = '```mermaid\ngraph TD;\nA-->B;\n```';
    render(<MarkdownViewer text={md} inlineMode />);
    await waitFor(() => {
      expect(document.querySelector('[data-testid="mermaid-svg"]')).toBeInTheDocument();
    });
  });

  it('preprocesses HTML video tags into playable video elements', () => {
    const md = '<video src="/media/demo.mp4" width="320" height="240"></video>';
    render(<MarkdownViewer text={md} inlineMode />);
    const video = document.querySelector('video');
    expect(video).not.toBeNull();
    expect(video?.getAttribute('src')).toBe('/media/demo.mp4');
  });

  it('preprocesses HTML audio tags into audio elements', () => {
    const md = '<audio src="/media/note.mp3"></audio>';
    render(<MarkdownViewer text={md} inlineMode />);
    const audio = document.querySelector('audio');
    expect(audio).not.toBeNull();
    expect(audio?.getAttribute('src')).toBe('/media/note.mp3');
  });

  it('renders markdown tables', () => {
    const md = '| Col A | Col B |\n| --- | --- |\n| one | two |';
    render(<MarkdownViewer text={md} inlineMode />);
    expect(screen.getByRole('table')).toBeInTheDocument();
    expect(screen.getByText('Col A')).toBeInTheDocument();
    expect(screen.getByText('two')).toBeInTheDocument();
  });

  it('loads authenticated notebook images via the API helper', async () => {
    const projectId = '11111111-1111-1111-1111-111111111111';
    const notebookId = '22222222-2222-2222-2222-222222222222';
    const md = '![chart](./Output/chart.png)';

    render(
      <MarkdownViewer
        text={md}
        inlineMode
        projectId={projectId}
        notebookId={notebookId}
        basePath="Output"
      />,
    );

    await waitFor(() => {
      expect(getAuthenticatedUrl).toHaveBeenCalled();
    });

    const img = await screen.findByRole('img', { name: 'chart' }) as HTMLImageElement;
    expect(img.src).toContain('blob:mock-image');
  });

  it('downloads authenticated API links on click', async () => {
    getAuthenticatedUrl.mockResolvedValueOnce({
      objectUrl: 'blob:mock-download',
      fileName: 'report.pdf',
    });

    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const md = '[Download report](/api/projects/p1/files/f1/content)';

    render(<MarkdownViewer text={md} inlineMode />);
    const link = screen.getByRole('link', { name: 'Download report' });
    link.dispatchEvent(new MouseEvent('click', { bubbles: true }));

    await waitFor(() => {
      expect(getAuthenticatedUrl).toHaveBeenCalled();
      expect(clickSpy).toHaveBeenCalled();
    });

    clickSpy.mockRestore();
  });

  it('opens external links with electron when available', async () => {
    const openExternal = vi.fn();
    (window as unknown as { electron: { openExternal: typeof openExternal } }).electron = {
      openExternal,
    };

    const md = '[External](https://example.com/docs)';
    render(<MarkdownViewer text={md} inlineMode />);

    const link = screen.getByRole('link', { name: 'External' });
    link.dispatchEvent(new MouseEvent('click', { bubbles: true }));

    await waitFor(() => {
      expect(openExternal).toHaveBeenCalledWith('https://example.com/docs');
    });
  });

  it('resolves project files through the lookup callback', async () => {
    const resolveProjectFilePath = vi.fn(() => 'file-abc');
    const projectId = '11111111-1111-1111-1111-111111111111';
    const md = '![diagram](./assets/diagram.png)';

    render(
      <MarkdownViewer
        text={md}
        inlineMode
        projectId={projectId}
        resolveProjectFilePath={resolveProjectFilePath}
      />,
    );

    await waitFor(() => {
      expect(resolveProjectFilePath).toHaveBeenCalledWith('assets/diagram.png');
      expect(getAuthenticatedUrl).toHaveBeenCalled();
    });
  });

  it('loads authenticated notebook video content', async () => {
    getAuthenticatedUrl.mockResolvedValueOnce({
      objectUrl: 'blob:mock-video',
      fileName: 'clip.mp4',
    });

    const projectId = '11111111-1111-1111-1111-111111111111';
    const notebookId = '22222222-2222-2222-2222-222222222222';
    const md = '<video src="./media/clip.mp4" width="640" height="360"></video>';

    render(
      <MarkdownViewer
        text={md}
        inlineMode
        projectId={projectId}
        notebookId={notebookId}
        basePath="Output"
      />,
    );

    await waitFor(() => {
      expect(getAuthenticatedUrl).toHaveBeenCalled();
    });
    const video = document.querySelector('video') as HTMLVideoElement | null;
    expect(video).not.toBeNull();
    expect(video?.src).toContain('blob:mock-video');
  });

  it('preprocesses audio tags with nested source elements', () => {
    const md = '<audio controls><source src="/media/note.mp3"></source></audio>';
    render(<MarkdownViewer text={md} inlineMode />);
    const audio = document.querySelector('audio');
    expect(audio).not.toBeNull();
    expect(audio?.getAttribute('src')).toBe('/media/note.mp3');
  });

  it('shows image unavailable when authenticated fetch fails', async () => {
    getAuthenticatedUrl.mockRejectedValueOnce(Object.assign(new Error('Forbidden'), { status: 403 }));
    const projectId = '11111111-1111-1111-1111-111111111111';
    const notebookId = '22222222-2222-2222-2222-222222222222';

    render(
      <MarkdownViewer
        text="![broken](./missing.png)"
        inlineMode
        projectId={projectId}
        notebookId={notebookId}
      />,
    );

    expect(await screen.findByText('Image unavailable')).toBeInTheDocument();
  });

  it('renders large markdown without GFM plugins', () => {
    const largeText = `# Large doc\n\n${'paragraph '.repeat(25000)}`;
    render(<MarkdownViewer text={largeText} inlineMode />);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Large doc');
  });

  it('renders media tokens inside table cells', async () => {
    getAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:mock-table-video',
      fileName: 'clip.mp4',
    });

    const md = '| Media |\n| --- |\n| [VIDEO:./clip.mp4] |';
    render(<MarkdownViewer text={md} inlineMode projectId="p1" notebookId="n1" />);

    await waitFor(() => {
      expect(document.querySelector('table video')).not.toBeNull();
    });
  });

  it('preprocesses video tags with width and height metadata', () => {
    const md = '<video controls src="./media/clip.mp4" width="640" height="360"></video>';
    render(
      <MarkdownViewer
        text={md}
        inlineMode
        projectId="11111111-1111-1111-1111-111111111111"
        notebookId="22222222-2222-2222-2222-222222222222"
        basePath="Output"
      />,
    );
    const video = document.querySelector('video') as HTMLVideoElement | null;
    expect(video).not.toBeNull();
    expect(video?.style.width).toBe('640px');
    expect(video?.style.height).toBe('360px');
  });

  it('downloads relative notebook links through authenticated handler', async () => {
    getAuthenticatedUrl.mockResolvedValueOnce({
      objectUrl: 'blob:notebook-file',
      fileName: 'notes.pdf',
    });
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    render(
      <MarkdownViewer
        text="[Notes](./notes.pdf)"
        inlineMode
        projectId="11111111-1111-1111-1111-111111111111"
        notebookId="22222222-2222-2222-2222-222222222222"
        basePath="docs"
      />,
    );

    const link = screen.getByRole('link', { name: 'Notes' });
    link.dispatchEvent(new MouseEvent('click', { bubbles: true }));

    await waitFor(() => {
      expect(getAuthenticatedUrl).toHaveBeenCalled();
    });
    expect(clickSpy).toHaveBeenCalled();

    clickSpy.mockRestore();
  });
}); 
