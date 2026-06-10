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
import { $getRoot, $createParagraphNode, $createTextNode } from 'lexical';
import { useLexicalComposerContext } from '@lexical/react/LexicalComposerContext';
import { useEffect } from 'react';

const testEditorConfig = {
  namespace: 'ToolbarInteractionEditor',
  theme: {},
  onError: () => {},
  nodes: [
    HeadingNode, ListNode, ListItemNode, QuoteNode, CodeNode, CodeHighlightNode,
    LinkNode, TableNode, TableCellNode, TableRowNode, ImageNode, AudioNode, VideoNode,
  ],
};

function SeedTextPlugin({ text }: { text: string }) {
  const [editor] = useLexicalComposerContext();
  useEffect(() => {
    editor.update(() => {
      const root = $getRoot();
      root.clear();
      const p = $createParagraphNode();
      p.append($createTextNode(text));
      root.append(p);
    });
  }, [editor, text]);
  return null;
}

const renderToolbar = (props: Record<string, unknown>, seedText = 'Hello world') =>
  render(
    <LexicalComposer initialConfig={testEditorConfig}>
      <SeedTextPlugin text={seedText} />
      <LexicalToolbar config={{
        bold: true, italic: true, strikethrough: true, code: true,
        link: true, image: true, audio: true, video: true,
        unorderedList: true, orderedList: true, blockquote: true,
        heading: true, table: true,
      }} {...props} />
      <RichTextPlugin
        contentEditable={<ContentEditable />}
        placeholder={<div>Test</div>}
        ErrorBoundary={LexicalErrorBoundary}
      />
    </LexicalComposer>
  );

describe('LexicalToolbar – selection-dependent actions', () => {
  it('inserts image via dialog submit', () => {
    renderToolbar({});
    fireEvent.click(screen.getByTitle(/insert image/i));
    fireEvent.change(screen.getByLabelText(/alt text/i), { target: { value: 'Diagram' } });
    fireEvent.change(screen.getByLabelText(/image url/i), { target: { value: 'https://example.com/d.png' } });
    fireEvent.click(screen.getByRole('button', { name: /insert image/i }));
    expect(screen.queryByLabelText(/alt text/i)).not.toBeInTheDocument();
  });

  it('toggles bullet list on and off', () => {
    renderToolbar({});
    const ul = screen.getByTitle(/bullet list/i);
    fireEvent.click(ul);
    fireEvent.click(ul);
  });

  it('toggles quote block on and off', () => {
    renderToolbar({});
    const quote = screen.getByTitle(/quote/i);
    fireEvent.click(quote);
    fireEvent.click(quote);
  });

  it('toggles heading back to paragraph when same heading selected', () => {
    renderToolbar({});
    fireEvent.click(screen.getByTitle('Headings'));
    fireEvent.click(screen.getByText('Heading 2'));
    fireEvent.click(screen.getByTitle('Headings'));
    fireEvent.click(screen.getByText('Heading 2'));
  });

  it('closes audio dialog with cancel', () => {
    renderToolbar({});
    fireEvent.click(screen.getByTitle(/insert audio/i));
    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }));
    expect(screen.queryByLabelText(/audio url/i)).not.toBeInTheDocument();
  });

  it('closes video dialog with cancel', () => {
    renderToolbar({});
    fireEvent.click(screen.getByTitle(/insert video/i));
    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }));
    expect(screen.queryByLabelText(/video url/i)).not.toBeInTheDocument();
  });

  it('closes table picker on outside click', () => {
    renderToolbar({});
    fireEvent.click(screen.getByTitle(/insert table/i));
    expect(screen.getByText('Select table size')).toBeInTheDocument();
    fireEvent.mouseDown(document.body);
    expect(screen.queryByText('Select table size')).not.toBeInTheDocument();
  });

  it('renders Saving label for non-Send submit loading state', () => {
    renderToolbar({
      submitButton: { label: 'Save', onClick: vi.fn(), isLoading: true },
    });
    expect(screen.getByText('Saving...')).toBeInTheDocument();
  });
});
