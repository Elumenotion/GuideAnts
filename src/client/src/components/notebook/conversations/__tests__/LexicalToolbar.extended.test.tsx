import React from 'react';
import { render, fireEvent, screen } from '../../../../test/test-utils';
import LexicalToolbar from '../LexicalToolbar';
import { describe, it, expect, vi } from 'vitest';
import { LexicalComposer } from '@lexical/react/LexicalComposer';
import { RichTextPlugin } from '@lexical/react/LexicalRichTextPlugin';
import { ContentEditable } from '@lexical/react/LexicalContentEditable';
import { HeadingNode, QuoteNode } from '@lexical/rich-text';
import { ListItemNode, ListNode } from '@lexical/list';
import { CodeHighlightNode, CodeNode } from '@lexical/code';
import { LinkNode } from '@lexical/link';
import { TableCellNode, TableNode, TableRowNode } from '@lexical/table';
import { LexicalErrorBoundary } from '@lexical/react/LexicalErrorBoundary';
import { ImageNode } from '../ImageNode';
import { AudioNode } from '../AudioNode';
import { VideoNode } from '../VideoNode';

const testEditorConfig = {
  namespace: 'TestEditorExtended',
  theme: {},
  onError: () => {},
  nodes: [
    HeadingNode, ListNode, ListItemNode, QuoteNode, CodeNode, CodeHighlightNode,
    LinkNode, TableNode, TableCellNode, TableRowNode, ImageNode, AudioNode, VideoNode,
  ],
};

const renderWithLexical = (toolbarProps: Record<string, unknown>) => render(
  <LexicalComposer initialConfig={testEditorConfig}>
    <div>
      <LexicalToolbar config={{}} {...toolbarProps} />
      <RichTextPlugin
        contentEditable={<ContentEditable />}
        placeholder={<div>Test</div>}
        ErrorBoundary={LexicalErrorBoundary}
      />
    </div>
  </LexicalComposer>
);

const fullConfig = {
  bold: true,
  italic: true,
  strikethrough: true,
  code: true,
  link: true,
  image: true,
  audio: true,
  video: true,
  unorderedList: true,
  orderedList: true,
  blockquote: true,
  heading: true,
  table: true,
};

describe('LexicalToolbar – extended interactions', () => {
  it('dispatches italic, strikethrough, and inline code formats', () => {
    const { getByTitle } = renderWithLexical({ config: fullConfig });
    fireEvent.click(getByTitle(/italic/i));
    fireEvent.click(getByTitle(/strikethrough/i));
    fireEvent.click(getByTitle(/inline code/i));
  });

  it('opens audio dialog and inserts audio on submit', () => {
    const { getByTitle, getByLabelText, getByRole } = renderWithLexical({ config: { audio: true } });
    fireEvent.click(getByTitle(/insert audio/i));
    fireEvent.change(getByLabelText(/audio url/i), { target: { value: './clip.mp3' } });
    fireEvent.click(getByRole('button', { name: /insert audio/i }));
  });

  it('opens video dialog and inserts video on submit', () => {
    const { getByTitle, getByLabelText, getByRole } = renderWithLexical({ config: { video: true } });
    fireEvent.click(getByTitle(/insert video/i));
    fireEvent.change(getByLabelText(/video url/i), { target: { value: './clip.mp4' } });
    fireEvent.click(getByRole('button', { name: /insert video/i }));
  });

  it('opens table picker and inserts via grid selection', () => {
    const { getByTitle, getByText } = renderWithLexical({ config: { table: true } });
    fireEvent.click(getByTitle(/insert table/i));
    expect(getByText('Select table size')).toBeInTheDocument();

    const grid = document.querySelector('.grid.gap-1');
    expect(grid).toBeTruthy();
    const cell = grid!.children[2] as HTMLElement;
    fireEvent.mouseEnter(cell);
    fireEvent.click(cell);
  });

  it('opens custom table size dialog from picker', () => {
    const { getByTitle, getByText, getByLabelText, getByRole } = renderWithLexical({ config: { table: true } });
    fireEvent.click(getByTitle(/insert table/i));
    fireEvent.click(getByText('Custom size...'));
    expect(getByText('Custom Table Size')).toBeInTheDocument();
    fireEvent.change(getByLabelText(/^rows$/i), { target: { value: '3' } });
    fireEvent.change(getByLabelText(/^columns$/i), { target: { value: '2' } });
    fireEvent.click(getByRole('button', { name: /insert table/i }));
  });

  it('shows h4 and h5 heading options', () => {
    const { getByText } = renderWithLexical({ config: { heading: true } });
    fireEvent.click(getByText('H'));
    expect(getByText('Heading 4')).toBeInTheDocument();
    expect(getByText('Heading 5')).toBeInTheDocument();
    fireEvent.click(getByText('Heading 4'));
  });

  it('toggles source mode via toolbar button', () => {
    const onToggle = vi.fn();
    const { getByTitle } = renderWithLexical({
      config: { bold: true },
      isSourceMode: false,
      onToggleSourceMode: onToggle,
    });
    fireEvent.click(getByTitle(/toggle markdown source/i));
    expect(onToggle).toHaveBeenCalled();
  });

  it('shows HTML preview indicator and hides formatting in html preview mode', () => {
    const { getByText, queryByTitle } = renderWithLexical({
      config: fullConfig,
      isHtmlPreviewMode: true,
      isSourceMode: false,
    });
    expect(getByText('HTML Preview (read-only)')).toBeInTheDocument();
    expect(queryByTitle(/bold/i)).not.toBeInTheDocument();
  });

  it('disables formatting buttons while in source mode', () => {
    const { getByTitle } = renderWithLexical({
      config: fullConfig,
      isSourceMode: true,
      onToggleSourceMode: vi.fn(),
    });
    expect(getByTitle(/bold/i)).toBeDisabled();
  });

  it('renders loading state on submit button', () => {
    const { getByText } = renderWithLexical({
      config: { bold: true },
      submitButton: { label: 'Send', onClick: vi.fn(), isLoading: true },
    });
    expect(getByText('Sending...')).toBeInTheDocument();
  });

  it('calls fullScreenButton onClick', () => {
    const onFullScreen = vi.fn();
    const { getByLabelText } = renderWithLexical({
      config: { bold: true },
      fullScreenButton: { onClick: onFullScreen },
    });
    fireEvent.click(getByLabelText('Full screen'));
    expect(onFullScreen).toHaveBeenCalled();
  });

  it('closes link dialog via cancel without inserting', () => {
    const { getByTitle, getByRole, queryByLabelText } = renderWithLexical({ config: { link: true } });
    fireEvent.click(getByTitle(/insert link/i));
    fireEvent.click(getByRole('button', { name: /^cancel$/i }));
    expect(queryByLabelText(/link text/i)).not.toBeInTheDocument();
  });

  it('closes heading dropdown when clicking outside', () => {
    const { getByText, queryByText } = renderWithLexical({ config: { heading: true } });
    fireEvent.click(getByText('H'));
    expect(queryByText('Heading 1')).toBeInTheDocument();
    fireEvent.mouseDown(document.body);
    expect(queryByText('Heading 1')).not.toBeInTheDocument();
  });
});
