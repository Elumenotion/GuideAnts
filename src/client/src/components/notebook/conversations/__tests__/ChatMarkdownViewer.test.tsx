import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render } from '@testing-library/react';
import '@testing-library/jest-dom';

// Mock MermaidRenderer so we can assert invocation without rendering SVG
vi.mock('../../../common/MermaidRenderer', () => ({
  __esModule: true,
  default: ({ chart }: { chart: string }) => (
    <div data-testid="mermaid" data-chart={chart}></div>
  ),
}));

const { default: ChatMarkdownViewer } = await import('../ChatMarkdownViewer');

const InlinePngDataUrl =
  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6Qx0sAAAAASUVORK5CYII=';

describe('ChatMarkdownViewer', () => {
  it('renders basic markdown elements', () => {
    const md = '# Heading\n\nThis is **bold** and a [link](https://example.com).';
    const { getByText, getByRole } = render(<ChatMarkdownViewer text={md} />);

    expect(getByText('Heading')).toBeInTheDocument();
    expect(getByText('bold').tagName.toLowerCase()).toBe('strong');
    const link = getByRole('link', { name: 'link' });
    expect(link).toHaveAttribute('href', 'https://example.com');
  });

  it('passes mermaid code blocks to MermaidRenderer', () => {
    const diagram = 'graph TD;\nA-->B;';
    const md = '```mermaid\n' + diagram + '\n```';
    const { getByTestId } = render(<ChatMarkdownViewer text={md} />);
    const node = getByTestId('mermaid');
    expect(node).toHaveAttribute('data-chart', diagram);
  });

  it('sanitises script tags', () => {
    const md = 'Hello<script>window.evil = true</script>world';
    const { queryByText } = render(<ChatMarkdownViewer text={md} />);
    // The literal script content should not be present
    expect(queryByText('window.evil = true')).not.toBeInTheDocument();
  });

  it('renders inline base64 image data URLs', () => {
    const md = `![tiny](${InlinePngDataUrl})`;
    const { getByRole } = render(<ChatMarkdownViewer text={md} />);

    const img = getByRole('img', { name: 'tiny' }) as HTMLImageElement;
    expect(img).toBeInTheDocument();
    expect(img.getAttribute('src')).toContain('data:image/png;base64,');
  });

  it('keeps non-image data URLs sanitized', () => {
    const dataTextUrl = 'data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==';
    const { getByText } = render(<ChatMarkdownViewer text={`[unsafe-link](${dataTextUrl})`} />);

    const link = getByText('unsafe-link').closest('a') as HTMLAnchorElement | null;
    expect(link).not.toBeNull();
    const href = link?.getAttribute('href') || '';
    expect(href).not.toContain('data:text/html');
  });
}); 
