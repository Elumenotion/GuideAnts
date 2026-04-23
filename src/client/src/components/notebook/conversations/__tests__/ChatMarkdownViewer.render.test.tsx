import React from 'react';
import { render } from '../../../../test/test-utils';
import ChatMarkdownViewer from '../ChatMarkdownViewer';
import { describe, it, expect, vi } from 'vitest';

// Mock MermaidRenderer to a simple div so we can detect it
vi.mock('../../../common/MermaidRenderer', () => ({
  default: ({ chart }: { chart: string }) => <div data-testid="mermaid">{chart}</div>,
}));

describe('ChatMarkdownViewer', () => {
  it('renders mermaid diagram, table and strips script tags', () => {
    const markdown = [
      '# Title',
      '',
      '```mermaid',
      'graph TD; A-->B;',
      '```',
      '',
      '| Col1 | Col2 |',
      '| ---- | ---- |',
      '| A | B |',
      '',
      '<script>alert("x")</script>'
    ].join('\n');

    const { getByTestId, queryByText, getByRole } = render(
      <ChatMarkdownViewer text={markdown} />
    );

    // Mermaid placeholder rendered
    expect(getByTestId('mermaid')).toBeInTheDocument();

    // Table
    const table = getByRole('table');
    expect(table).toBeInTheDocument();

    // Script tag should be rendered as text, not as an element
    expect(getByTestId('markdown-viewer').textContent).toContain('<script>alert("x")</script>');
  });
}); 