import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '../../../test/test-utils';
import MarkdownViewer from '../MarkdownViewer';
import '@testing-library/jest-dom';
import { waitFor } from '@testing-library/react';

// Mock for mermaid so dynamic import resolves during tests
vi.mock('mermaid', () => ({
  default: {
    initialize: vi.fn(),
    render: vi.fn().mockResolvedValue({ svg: '<svg></svg>' }),
  },
}));

const InlinePngDataUrl =
  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6Qx0sAAAAASUVORK5CYII=';

describe('MarkdownViewer', () => {
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

  it('renders mermaid code block using MermaidRenderer', async () => {
    const md = '```mermaid\ngraph TD; A-->B\n```';
    const { container } = render(<MarkdownViewer text={md} />);

    // Wait for MermaidRenderer to insert SVG
    await waitFor(() => {
      const diagramContainer = container.querySelector('.mermaid-diagram');
      expect(diagramContainer).toBeInTheDocument();
      // svg inserted by mocked mermaid.render
      expect(diagramContainer?.querySelector('svg')).toBeInTheDocument();
    });
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
}); 
