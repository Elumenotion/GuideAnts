import React from 'react';
import { render, fireEvent } from '../../../../test/test-utils';
import LexicalToolbar from '../LexicalToolbar';
import { FORMAT_TEXT_COMMAND } from 'lexical';
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

// Full Lexical editor config for testing
const testEditorConfig = {
  namespace: 'TestEditor',
  theme: {},
  onError: () => {},
  nodes: [HeadingNode, ListNode, ListItemNode, QuoteNode, CodeNode, CodeHighlightNode, LinkNode, TableNode, TableCellNode, TableRowNode, ImageNode],
};

// Helper to render toolbar within real Lexical context
const renderWithLexical = (toolbarProps: any) => {
  return render(
    <LexicalComposer initialConfig={testEditorConfig}>
      <div>
        <LexicalToolbar {...toolbarProps} />
        <RichTextPlugin
          contentEditable={<ContentEditable />}
          placeholder={<div>Test</div>}
          ErrorBoundary={LexicalErrorBoundary}
        />
      </div>
    </LexicalComposer>
  );
};

describe('LexicalToolbar – commands with real Lexical', () => {
  it('dispatches bold command when Bold button clicked', () => {
    const { getByTitle } = renderWithLexical({
      config: { bold: true }
    });

    const boldButton = getByTitle(/bold/i);
    expect(boldButton).toBeInTheDocument();
    
    // Click should not throw - format will be applied to empty editor
    fireEvent.click(boldButton);
  });

  it('opens and shows heading options in dropdown', () => {
    const { getByText, queryByText } = renderWithLexical({
      config: { heading: true }
    });

    // Initially closed
    expect(queryByText('Heading 1')).not.toBeInTheDocument();
    
    // Open dropdown
    fireEvent.click(getByText('H'));
    expect(queryByText('Heading 1')).toBeInTheDocument();
    expect(queryByText('Heading 2')).toBeInTheDocument();
    expect(queryByText('Heading 3')).toBeInTheDocument();
  });

  it('applies heading format when option selected', () => {
    const { getByText, queryByText } = renderWithLexical({
      config: { heading: true }
    });

    // Open dropdown and click Heading 2
    fireEvent.click(getByText('H'));
    
    // Should not throw when applying heading format
    fireEvent.click(getByText('Heading 2'));
    
    // Dropdown should close after selection
    expect(queryByText('Heading 1')).not.toBeInTheDocument();
  });

  it('dispatches list commands when list buttons clicked', () => {
    const { getByTitle } = renderWithLexical({
      config: { unorderedList: true, orderedList: true }
    });

    const ulButton = getByTitle(/bullet list/i);
    const olButton = getByTitle(/numbered list/i);
    
    // Should not throw when applying list formats
    fireEvent.click(ulButton);
    fireEvent.click(olButton);
  });

  it('opens link dialog when link button clicked', () => {
    const { getByTitle, queryByLabelText } = renderWithLexical({
      config: { link: true }
    });

    const linkButton = getByTitle(/insert link/i);
    fireEvent.click(linkButton);
    
    // Link dialog should appear - check for the form inputs
    expect(queryByLabelText(/link text/i)).toBeInTheDocument();
    expect(queryByLabelText(/url/i)).toBeInTheDocument();
  });

  it('inserts link when dialog form submitted', () => {
    const { getByTitle, getByLabelText, getByRole } = renderWithLexical({
      config: { link: true }
    });

    // Open link dialog
    fireEvent.click(getByTitle(/insert link/i));
    
    // Fill form
    fireEvent.change(getByLabelText(/link text/i), { target: { value: 'Test Link' } });
    fireEvent.change(getByLabelText(/url/i), { target: { value: 'https://example.com' } });
    
    // Submit - should not throw (use button role to avoid header conflict)
    fireEvent.click(getByRole('button', { name: /insert link/i }));
  });

  it('opens image dialog when image button clicked', () => {
    const { getByTitle, queryByLabelText } = renderWithLexical({
      config: { image: true }
    });

    const imageButton = getByTitle(/insert image/i);
    fireEvent.click(imageButton);
    
    // Image dialog should appear - check for the form inputs
    expect(queryByLabelText(/alt text/i)).toBeInTheDocument();
    expect(queryByLabelText(/image url/i)).toBeInTheDocument();
  });

  it('applies quote format when quote button clicked', () => {
    const { getByTitle } = renderWithLexical({
      config: { blockquote: true }
    });

    const quoteButton = getByTitle(/quote/i);
    
    // Should not throw when applying quote format
    fireEvent.click(quoteButton);
  });

  it('inserts table when table button clicked', () => {
    const { getByTitle } = renderWithLexical({
      config: { table: true }
    });

    const tableButton = getByTitle(/insert table/i);
    
    // Should not throw when inserting table
    fireEvent.click(tableButton);
  });

  it('shows all formatting buttons when config enabled', () => {
    const { getByTitle } = renderWithLexical({
      config: {
        bold: true,
        italic: true,
        strikethrough: true,
        code: true,
        link: true,
        image: true,
        unorderedList: true,
        orderedList: true,
        blockquote: true,
        heading: true,
        table: true
      }
    });

    // All buttons should be present
    expect(getByTitle(/bold/i)).toBeInTheDocument();
    expect(getByTitle(/italic/i)).toBeInTheDocument();
    expect(getByTitle(/strikethrough/i)).toBeInTheDocument();
    expect(getByTitle(/inline code/i)).toBeInTheDocument();
    expect(getByTitle(/insert link/i)).toBeInTheDocument();
    expect(getByTitle(/insert image/i)).toBeInTheDocument();
    expect(getByTitle(/bullet list/i)).toBeInTheDocument();
    expect(getByTitle(/numbered list/i)).toBeInTheDocument();
    expect(getByTitle(/quote/i)).toBeInTheDocument();
    expect(getByTitle(/headings/i)).toBeInTheDocument();
    expect(getByTitle(/insert table/i)).toBeInTheDocument();
  });

  it('renders submit and cancel buttons when provided', () => {
    const onSubmit = vi.fn();
    const onCancel = vi.fn();
    
    const { getByText } = renderWithLexical({
      config: { bold: true },
      submitButton: { label: 'Save', onClick: onSubmit },
      cancelButton: { label: 'Cancel', onClick: onCancel }
    });

    const saveBtn = getByText('Save');
    const cancelBtn = getByText('Cancel');
    
    expect(saveBtn).toBeInTheDocument();
    expect(cancelBtn).toBeInTheDocument();
    
    fireEvent.click(saveBtn);
    fireEvent.click(cancelBtn);
    
    expect(onSubmit).toHaveBeenCalled();
    expect(onCancel).toHaveBeenCalled();
  });
}); 