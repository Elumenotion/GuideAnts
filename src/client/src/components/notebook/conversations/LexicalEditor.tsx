import { LexicalComposer } from '@lexical/react/LexicalComposer';
import { RichTextPlugin } from '@lexical/react/LexicalRichTextPlugin';
import { ContentEditable } from '@lexical/react/LexicalContentEditable';
import { HistoryPlugin } from '@lexical/react/LexicalHistoryPlugin';
import { AutoFocusPlugin } from '@lexical/react/LexicalAutoFocusPlugin';
import { LinkPlugin } from '@lexical/react/LexicalLinkPlugin';
import { ListPlugin } from '@lexical/react/LexicalListPlugin';
import { MarkdownShortcutPlugin } from '@lexical/react/LexicalMarkdownShortcutPlugin';
import { TablePlugin } from '@lexical/react/LexicalTablePlugin';
import { HeadingNode, QuoteNode } from '@lexical/rich-text';
import { ListItemNode, ListNode } from '@lexical/list';
import { CodeHighlightNode, CodeNode } from '@lexical/code';
import { LinkNode } from '@lexical/link';
import { TableCellNode, TableNode, TableRowNode } from '@lexical/table';
import { TRANSFORMERS, $convertFromMarkdownString, $convertToMarkdownString } from '@lexical/markdown';
import { ImageNode, IMAGE_TRANSFORMER, ImageNodeContextProvider } from './ImageNode';
import { AudioNode, AUDIO_TRANSFORMER, $isAudioNode, $createAudioNode } from './AudioNode';
import { VideoNode, VIDEO_TRANSFORMER, $isVideoNode, $createVideoNode } from './VideoNode';
import { LexicalErrorBoundary } from '@lexical/react/LexicalErrorBoundary';
import { useLexicalComposerContext } from '@lexical/react/LexicalComposerContext';
import { $getRoot } from 'lexical';
import type { EditorState } from 'lexical';
import { forwardRef, useImperativeHandle, useEffect, useState, useRef, useMemo } from 'react';
// import MarkdownPlugin from './MarkdownPlugin';
import LexicalToolbar from './LexicalToolbar';
import type { MultilineElementTransformer } from '@lexical/markdown';
import { $isTableNode, $isTableRowNode, $isTableCellNode, $createTableNode, $createTableRowNode, $createTableCellNode } from '@lexical/table';
import { $createTextNode, $createParagraphNode, LexicalNode } from 'lexical';
import { $isImageNode } from './ImageNode';
import { encodeMarkdownLinkSpaces } from '../../../utils/markdownLinkEncoding';

/**
 * Decodes HTML numeric character entities (e.g., &#32;) back to their actual characters.
 * This fixes Lexical's incorrect encoding of spaces within formatted text that contains inline code.
 */
function decodeHtmlEntities(markdown: string): string {
  return markdown.replace(/&#(\d+);/g, (_, codePoint) => 
    String.fromCodePoint(parseInt(codePoint, 10))
  );
}

/**
 * Removes unnecessary escape sequences that Lexical adds before markdown special characters.
 * Lexical escapes characters like *, _, [, ], etc. even when they're part of valid markdown syntax.
 * This causes issues when toggling between source mode and WYSIWYG mode.
 * 
 * We specifically unescape:
 * - \* → * (asterisks for bold/italic)
 * - \_ → _ (underscores for bold/italic)  
 * - \[ and \] → [ and ] (for links)
 * - \` → ` (for inline code)
 * 
 * But we preserve escapes that are likely intentional (preceded by another backslash).
 */
function cleanMarkdownEscapes(markdown: string): string {
  // Match backslash followed by markdown special chars, but not double backslash
  // Use negative lookbehind to avoid matching \\* as \* 
  return markdown
    .replace(/(?<!\\)\\(\*)/g, '$1')  // \* → *
    .replace(/(?<!\\)\\_/g, '_')      // \_ → _
    .replace(/(?<!\\)\\\[/g, '[')     // \[ → [
    .replace(/(?<!\\)\\\]/g, ']')     // \] → ]
    .replace(/(?<!\\)\\`/g, '`');     // \` → `
}

/**
 * Fixes malformed adjacent format markers that Lexical produces.
 * When bold text is immediately followed by italic text (or vice versa),
 * Lexical outputs incorrect markers like **bold*italic*** instead of **bold***italic*
 * 
 * This function detects and fixes these patterns by ensuring proper marker closure.
 */
function fixAdjacentFormatMarkers(markdown: string): string {
  let result = markdown;
  
  // Fix pattern: **text*text*** → **text***text*
  // This happens when bold text is followed by bold+italic text
  // The *** at the end should close bold first, then we need proper italic markers
  
  // Pattern: **word1*word2*** where word2 should be italic only or bold+italic
  // We need to detect when *** appears and the structure suggests improper nesting
  
  // Fix: Look for **...***...*** and restructure
  // Match: **text*moretext*** - bold followed by italic that closes wrong
  result = result.replace(/\*\*([^*]+)\*([^*]+)\*\*\*/g, (_match, boldPart, italicPart) => {
    // Convert **bold*italic*** to **bold***italic*
    return `**${boldPart}***${italicPart}*`;
  });
  
  // Also fix the reverse: *text**moretext*** (italic then bold)
  result = result.replace(/\*([^*]+)\*\*([^*]+)\*\*\*/g, (_match, italicPart, boldPart) => {
    // Convert *italic**bold*** to *italic***bold**
    return `*${italicPart}***${boldPart}**`;
  });
  
  return result;
}

// Local preprocessor for HTML media tags → token form so transformers can import nodes
function preprocessHtmlMediaTags(text: string): string {
  let t = text;
  t = t.replace(/<video\s+[^>]*?>([\s\S]*?)<\/video>/gis, (match, content) => {
    const videoSrcMatch = match.match(/<video\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
    if (videoSrcMatch) return `[VIDEO:${videoSrcMatch[1]}]`;
    const sourceSrcMatch = content.match(/<source\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
    if (sourceSrcMatch) return `[VIDEO:${sourceSrcMatch[1]}]`;
    return match;
  });
  t = t.replace(/<audio\s+[^>]*?>([\s\S]*?)<\/audio>/gis, (match, content) => {
    const audioSrcMatch = match.match(/<audio\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
    if (audioSrcMatch) return `[AUDIO:${audioSrcMatch[1]}]`;
    const sourceSrcMatch = content.match(/<source\s+[^>]*?src\s*=\s*["']([^"']*?)["']/i);
    if (sourceSrcMatch) return `[AUDIO:${sourceSrcMatch[1]}]`;
    return match;
  });
  return t;
}

export interface ToolbarConfig {
  bold?: boolean;
  italic?: boolean;
  underline?: boolean;
  strikethrough?: boolean;
  code?: boolean;
  link?: boolean;
  unorderedList?: boolean;
  orderedList?: boolean;
  blockquote?: boolean;
  heading?: boolean;
  codeBlock?: boolean;
  table?: boolean;
  image?: boolean;
  audio?: boolean;
  video?: boolean;
}

export interface LexicalEditorProps {
  placeholder?: string;
  className?: string;
  autoFocus?: boolean;
  readOnly?: boolean;
  showToolbar?: boolean;
  toolbarConfig?: ToolbarConfig;
  toolbarClassName?: string;
  projectId?: string;
  notebookId?: string;
  resolveProjectFilePath?: (relativePath: string) => string | undefined;
  /** Base path for resolving relative URLs (directory of the markdown file, e.g., "Output" or "docs/guides") */
  basePath?: string;
  submitButton?: {
    label: string;
    onClick: () => void;
    disabled?: boolean;
    isLoading?: boolean;
  };
  cancelButton?: {
    label: string;
    onClick: () => void;
  };
  fullScreenButton?: {
    onClick: () => void;
  };
  onReady?: () => void;
}

export interface LexicalEditorRef {
  setValue: (markdown: string) => void;
  getValue: () => string;
  getIsEmpty: () => boolean;
  /**
   * Registers a listener that receives the latest markdown value whenever the editor state changes.
   * Returns an unregister function that should be called in a cleanup effect.
   */
  registerChangeListener: (callback: (markdown: string) => void) => () => void;
  /**
   * Toggles between WYSIWYG and source (raw markdown) editing modes.
   */
  toggleSourceMode: () => void;
  /**
   * Returns true if currently in source mode.
   */
  isSourceMode: () => boolean;
  /**
   * Inserts text at the current cursor position (or appends to the end).
   * Useful for speech-to-text and other text insertion features.
   */
  insertText: (text: string) => void;
}

export const DEFAULT_TOOLBAR_CONFIG: ToolbarConfig = {
  bold: true,
  italic: true,
  underline: false,      // Not standard markdown
  strikethrough: true,
  code: true,
  link: true,
  unorderedList: true,
  orderedList: true,
  blockquote: true,
  heading: true,
  codeBlock: true,
  table: true,
  image: true,
  audio: true,
  video: true,
};

// Proper table transformer for markdown tables
const TABLE_TRANSFORMER: MultilineElementTransformer = {
  dependencies: [TableNode, TableRowNode, TableCellNode],
  export: (node: any, _exportChildren: any) => {
    if (!$isTableNode(node)) {
      return null;
    }
    
    const rows = node.getChildren();
    if (rows.length === 0) return '';
    
    let markdown = '';
    
    rows.forEach((row: any, rowIndex: number) => {
      if ($isTableRowNode(row)) {
        const cells = row.getChildren();
        // Extract markdown for each cell, supporting images and mixed content
        const extractMarkdownFromNode = (node: LexicalNode): string => {
          // ImageNode → markdown syntactic form
          if ($isImageNode(node)) {
            const img = node as ImageNode;
            return `![${img.getAltText()}](${img.getSrc()})`;
          }

          // AudioNode → HTML form for markdown (handled by converters)
          if ($isAudioNode(node)) {
            const audio = node as AudioNode;
            return `<audio src="${audio.getSrc()}" controls></audio>`;
          }

          // VideoNode → HTML form for markdown (handled by converters)
          if ($isVideoNode(node)) {
            const video = node as VideoNode;
            return `<video src="${video.getSrc()}" controls></video>`;
          }

          // Text node – rely on plain text content
          if (typeof (node as any).getTextContent === 'function') {
            const text = (node as any).getTextContent();
            if (text) return text;
          }

          // Recurse into children to gather content (paragraphs, etc.)
          if (typeof (node as any).getChildren === 'function') {
            const childContent = (node as any).getChildren().map((child: LexicalNode) => extractMarkdownFromNode(child)).join(' ').trim();
            if (childContent) return childContent;
          }

          return '';
        };

        const cellValues = cells.map((cell: any) => {
          if ($isTableCellNode(cell)) {
            const md = extractMarkdownFromNode(cell).trim();
            return md || ' ';
          }
          return ' ';
        });
        
        // Add row content with proper padding
        const paddedCells = cellValues.map(cell => {
          const content = cell.trim() || '';
          // Pad to minimum width to match expected format
          return content.padEnd(Math.max(content.length, 8), ' ');
        });
        markdown += '| ' + paddedCells.join(' | ') + ' |\n';
        
        // Add header separator after first row
        if (rowIndex === 0) {
          markdown += '|' + paddedCells.map(() => '----------').join('|') + '|\n';
        }
      }
    });
    
    // Ensure a single trailing newline; avoid compounding extra blank lines
    if (!markdown.endsWith('\n')) markdown += '\n';
    return markdown;
  },
  regExpStart: /^\|(.+)\|$/,
  // Stop when a line does NOT start with a pipe (blank line or other content)
  regExpEnd: /^(?!\|).*$/,
  replace: (rootNode: any, _children: any, startMatch: any, _endMatch: any, linesInBetween: any, isImport: boolean) => {
    if (!isImport) return false;
    
    // Combine all lines for table parsing
    const allLines = [startMatch[0], ...(linesInBetween || [])];
    // Only contiguous table lines; ignore any trailing non-table lines defensively
    const tableLines = allLines.filter((line: string) => line.trim().startsWith('|'));
    
    if (tableLines.length < 2) return false; // Need at least header and separator
    
    // Parse header row
    const headerCells = tableLines[0].split('|').slice(1, -1).map((cell: string) => cell.trim());
    if (headerCells.length === 0) return false;
    
    // Skip separator row (tableLines[1])
    const dataRows = tableLines.slice(2);
    
    const tableNode = $createTableNode();
    
    // Helper to append parsed content into a cell
      const appendContentToCell = (cellNode: any, rawContent: string) => {
      const imageMatch = rawContent.match(/^!\[([^\]]*)\]\(([^)]+)\)$/);
      if (imageMatch) {
        const [, altText, src] = imageMatch;
        const imgParagraph = $createParagraphNode();
        const imgNode: any = new ImageNode(src, altText || '');
        imgParagraph.append(imgNode);
        cellNode.append(imgParagraph);
        return;
      }

      const audioMatch = rawContent.match(/^\[AUDIO:([^\]]+)\]$/);
      if (audioMatch) {
        const [, src] = audioMatch;
        const p = $createParagraphNode();
        p.append($createAudioNode({ src }));
        cellNode.append(p);
        return;
      }

      const videoMatch = rawContent.match(/^\[VIDEO:([^\]]+)\]$/);
      if (videoMatch) {
        const [, src] = videoMatch;
        const p = $createParagraphNode();
        p.append($createVideoNode({ src }));
        cellNode.append(p);
        return;
      }

      // Fallback to plain text paragraph
      if (rawContent) {
        const paragraph = $createParagraphNode();
        paragraph.append($createTextNode(rawContent));
        cellNode.append(paragraph);
      }
    };

    // Create header row
    const headerRow = $createTableRowNode();
    headerCells.forEach((cellText: string) => {
      const cell = $createTableCellNode(1); // header type
      appendContentToCell(cell, cellText);
      headerRow.append(cell);
    });
    tableNode.append(headerRow);
    
    // Create data rows
    dataRows.forEach((line: string) => {
      const cells = line.split('|').slice(1, -1).map((cell: string) => cell.trim());
      if (cells.length > 0) {
        const row = $createTableRowNode();
        
        // Ensure we have the same number of cells as header
        for (let i = 0; i < headerCells.length; i++) {
          const cell = $createTableCellNode(0); // regular type
          const cellText = cells[i] || '';
          appendContentToCell(cell, cellText);
          row.append(cell);
        }
        tableNode.append(row);
      }
    });
    
    rootNode.append(tableNode);
    return true;
  },
  type: 'multiline-element',
};

// Combined transformers - put IMAGE_TRANSFORMER first to process images before links
const ALL_TRANSFORMERS = [IMAGE_TRANSFORMER, VIDEO_TRANSFORMER, AUDIO_TRANSFORMER, ...TRANSFORMERS, TABLE_TRANSFORMER];

const editorConfig = {
  namespace: 'GuideAntsEditor',
  // The editor theme
  theme: {
    text: {
      bold: 'font-bold',
      italic: 'italic',
      strikethrough: 'line-through',
      underline: 'underline',
      code: 'bg-gray-100 px-1 py-0.5 rounded text-sm font-mono',
    },
    heading: {
      h1: 'text-3xl font-bold mb-4 mt-6',
      h2: 'text-2xl font-bold mb-3 mt-5',
      h3: 'text-xl font-bold mb-2 mt-4',
      h4: 'text-lg font-bold mb-2 mt-3',
      h5: 'text-base font-bold mb-1 mt-2',
    },
    quote: 'border-l-4 border-gray-300 pl-4 italic text-gray-600 my-4',
    list: {
      nested: {
        listitem: 'list-none',
      },
      ol: 'list-decimal list-inside my-2',
      ul: 'list-disc list-inside my-2',
      listitem: 'my-1',
    },
    code: 'bg-gray-100 p-4 rounded-md font-mono text-sm block my-4',
    codeHighlight: {
      atrule: 'text-purple-600',
      attr: 'text-blue-600',
      boolean: 'text-red-600',
      builtin: 'text-purple-600',
      cdata: 'text-gray-600',
      char: 'text-green-600',
      class: 'text-blue-600',
      'class-name': 'text-blue-600',
      comment: 'text-gray-500 italic',
      constant: 'text-red-600',
      deleted: 'text-red-600',
      doctype: 'text-gray-600',
      entity: 'text-red-600',
      function: 'text-purple-600',
      important: 'text-red-600 font-bold',
      inserted: 'text-green-600',
      keyword: 'text-purple-600',
      namespace: 'text-blue-600',
      number: 'text-red-600',
      operator: 'text-gray-700',
      prolog: 'text-gray-600',
      property: 'text-blue-600',
      punctuation: 'text-gray-700',
      regex: 'text-green-600',
      selector: 'text-green-600',
      string: 'text-green-600',
      symbol: 'text-red-600',
      tag: 'text-red-600',
      url: 'text-blue-600',
      variable: 'text-blue-600',
    },
    link: 'text-blue-600 underline hover:text-blue-800',
    table: 'border-collapse border border-gray-300 my-4',
    tableCell: 'border border-gray-300 px-3 py-2 min-w-[100px]',
    tableCellHeader: 'border border-gray-300 px-3 py-2 font-bold bg-gray-50 min-w-[100px]',
    tableRow: 'border-b border-gray-300',
  },
  // Handling of errors during update
  onError(error: Error) {
    console.error('Lexical Error:', error);
  },
  // Any custom nodes go here
  nodes: [
    HeadingNode,
    ListNode,
    ListItemNode,
    QuoteNode,
    CodeNode,
    CodeHighlightNode,
    LinkNode,
    TableNode,
    TableCellNode,
    TableRowNode,
    ImageNode,
    AudioNode,
    VideoNode,
  ],
};

// Plugin to expose editor methods via ref
function EditorRefPlugin({ 
  editorRef, 
  isSourceMode,
  setIsSourceMode,
  sourceText,
  setSourceText,
  lastExportedRef,
  listenerRef
}: { 
  editorRef: React.Ref<LexicalEditorRef>;
  isSourceMode: boolean;
  setIsSourceMode: (value: boolean) => void;
  sourceText: string;
  setSourceText: (value: string) => void;
  lastExportedRef: React.MutableRefObject<string | null>;
  listenerRef: React.MutableRefObject<((markdown: string) => void) | null>;
}) {
  const [registeredListener, setRegisteredListener] = useState<((markdown: string) => void) | null>(null);
  const [editor] = useLexicalComposerContext();

  // Sync state to ref for parent access
  useEffect(() => {
    listenerRef.current = registeredListener;
  }, [registeredListener, listenerRef]);

  useImperativeHandle(editorRef, () => ({
    setValue: (markdown: string) => {
      // Always store in sourceText for HTML content or source mode
      setSourceText(markdown);
      
      // If content is HTML, don't try to parse into Lexical (iframe will render it)
      if (isFullHtmlDocument(markdown)) {
        return;
      }
      
      if (!isSourceMode) {
        // In WYSIWYG mode with non-HTML content, update the editor
        editor.update(() => {
          const root = $getRoot();
          root.clear();
          
          if (markdown) {
            // Convert markdown to Lexical nodes using our transformers
            // Use shouldPreserveNewLines=true to maintain blank lines
            const mediaProcessed = preprocessHtmlMediaTags(encodeMarkdownLinkSpaces(markdown));
            $convertFromMarkdownString(mediaProcessed, ALL_TRANSFORMERS, undefined, true);
          }
        });
      }
    },
   
    getValue: () => {
      if (isSourceMode) {
        // In source mode, return the textarea content
        return sourceText;
      } else {
        // In WYSIWYG mode, extract from editor
        let markdown = '';
        editor.getEditorState().read(() => {
          const rawMarkdown = $convertToMarkdownString(ALL_TRANSFORMERS, undefined, true);
          // Decode HTML entities that Lexical incorrectly adds (e.g., &#32; for spaces)
          // clean up unnecessary escape sequences, and fix adjacent format markers
          markdown = fixAdjacentFormatMarkers(cleanMarkdownEscapes(decodeHtmlEntities(rawMarkdown)));
        });
        return markdown;
      }
    },

    getIsEmpty: () => {
      if (isSourceMode) {
        return sourceText.trim().length === 0;
      } else {
        let isEmpty = true;
        editor.getEditorState().read(() => {
          const rawMd = $convertToMarkdownString(ALL_TRANSFORMERS, undefined, true);
          const md = fixAdjacentFormatMarkers(cleanMarkdownEscapes(decodeHtmlEntities(rawMd))).trim();
          isEmpty = md.length === 0;
        });
        return isEmpty;
      }
    },

    // Register change listener that converts the updated editor state to markdown
    registerChangeListener: (callback: (markdown: string) => void) => {
      // Store the listener so we can call it from source mode too
      setRegisteredListener(() => callback);
      
      let timer: ReturnType<typeof setTimeout> | null = null;
      const flush = (editorState: EditorState) => {
        const rawMarkdown = editorState.read(() => $convertToMarkdownString(ALL_TRANSFORMERS, undefined, true));
        const markdown = fixAdjacentFormatMarkers(cleanMarkdownEscapes(decodeHtmlEntities(rawMarkdown)));
        callback(markdown);
      };
      const unregister = editor.registerUpdateListener(({ editorState }) => {
        if (timer) clearTimeout(timer as any);
        timer = setTimeout(() => flush(editorState as EditorState), 200);
      });
      return () => {
        unregister();
        setRegisteredListener(null);
      };
    },

    toggleSourceMode: () => {
      if (isSourceMode) {
        // Switching from source to WYSIWYG (or HTML preview)
        // Check if content is HTML - if so, just toggle mode (iframe will render it)
        if (isFullHtmlDocument(sourceText)) {
          setIsSourceMode(false);
          return;
        }
        
        // Parse the markdown from textarea into the editor
        try {
          editor.update(() => {
            const root = $getRoot();
            root.clear();
            
            if (sourceText) {
              const mediaProcessed = preprocessHtmlMediaTags(encodeMarkdownLinkSpaces(sourceText));
              // Use shouldPreserveNewLines=true to maintain blank lines
              $convertFromMarkdownString(mediaProcessed, ALL_TRANSFORMERS, undefined, true);
            }
          });
          setIsSourceMode(false);
        } catch (error) {
          console.error('Error parsing markdown:', error);
          // Stay in source mode if parsing fails
        }
      } else {
        // Switching from WYSIWYG (or HTML preview) to source
        // If content is HTML, sourceText is already the HTML - just toggle
        if (isFullHtmlDocument(sourceText)) {
          setIsSourceMode(true);
          return;
        }
        
        // Extract markdown from editor to textarea
        editor.getEditorState().read(() => {
          const rawMarkdown = $convertToMarkdownString(ALL_TRANSFORMERS, undefined, true);
          const markdown = fixAdjacentFormatMarkers(cleanMarkdownEscapes(decodeHtmlEntities(rawMarkdown)));
          setSourceText(markdown);
          // Remember exactly what we exported so we can detect unchanged toggles
          lastExportedRef.current = markdown;
        });
        setIsSourceMode(true);
      }
    },

    isSourceMode: () => isSourceMode,

    insertText: (text: string) => {
      if (isSourceMode) {
        // In source mode, append to the textarea content
        const newContent = sourceText + (sourceText && !sourceText.endsWith(' ') && !sourceText.endsWith('\n') ? ' ' : '') + text;
        setSourceText(newContent);
      } else {
        // In WYSIWYG mode, insert at cursor or append to end
        editor.update(() => {
          // Get current content, append the new text, and re-set
          const rawMarkdown = $convertToMarkdownString(ALL_TRANSFORMERS, undefined, true);
          const currentMarkdown = fixAdjacentFormatMarkers(cleanMarkdownEscapes(decodeHtmlEntities(rawMarkdown)));
          
          // Add space separator if needed
          const separator = currentMarkdown && !currentMarkdown.endsWith(' ') && !currentMarkdown.endsWith('\n') ? ' ' : '';
          const newMarkdown = currentMarkdown + separator + text;
          
          // Re-parse into editor
          const root = $getRoot();
          root.clear();
          if (newMarkdown) {
            const mediaProcessed = preprocessHtmlMediaTags(encodeMarkdownLinkSpaces(newMarkdown));
            $convertFromMarkdownString(mediaProcessed, ALL_TRANSFORMERS, undefined, true);
          }
        });
      }
    },
  }), [editor, isSourceMode, sourceText, setIsSourceMode, setSourceText]);

  return null;
}

function ReadyPlugin({ onReady }: { onReady?: () => void }) {
  useEffect(() => {
    if (onReady) onReady();
  }, [onReady]);
  return null;
}

/**
 * Detects if text is a full HTML document (starts with <!DOCTYPE html> or <html>).
 * These should be rendered in an iframe for proper display.
 */
function isFullHtmlDocument(text: string): boolean {
  const trimmed = text.trimStart();
  return /^(<!DOCTYPE\s+html|<html)/i.test(trimmed);
}

const LexicalEditor = forwardRef<LexicalEditorRef, LexicalEditorProps>(function LexicalEditor({
  placeholder = 'Enter some text...',
  className = '',
  autoFocus = false,
  readOnly = false,
  showToolbar = true,
  toolbarConfig = DEFAULT_TOOLBAR_CONFIG,
  toolbarClassName = '',
  projectId,
  notebookId,
  resolveProjectFilePath,
  basePath,
  submitButton,
  cancelButton,
  fullScreenButton,
  onReady,
}, ref) {
  const [isSourceMode, setIsSourceMode] = useState(false);
  const [sourceText, setSourceText] = useState('');
  
  // Detect if content is a full HTML document - should show iframe preview instead of WYSIWYG
  const isHtmlContent = useMemo(() => isFullHtmlDocument(sourceText), [sourceText]);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  // Track last exported markdown to make toggle idempotent
  const lastExportedRef = useRef<string | null>(null);

  // We need to access the ref created inside EditorRefPlugin to get the registered listener
  // But EditorRefPlugin uses the passed 'ref' which is forwarded.
  // We can't easily get the listener back out to the parent component (LexicalEditor)
  // without lifting the state up.
  // Actually, we can pass a mutable ref to EditorRefPlugin to hold the listener.
  const listenerRef = useRef<((markdown: string) => void) | null>(null);

  // Handle source textarea changes
  const handleSourceChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const newValue = e.target.value;
    setSourceText(newValue);
    // Notify listener if registered
    if (listenerRef.current) {
      listenerRef.current(newValue);
    }
  };

  // Handle keyboard shortcuts in source mode
  const handleSourceKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // Ctrl/Cmd + Enter to submit (if submit button exists)
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter' && submitButton) {
      e.preventDefault();
      submitButton.onClick();
    }
  };

  return (
    <ImageNodeContextProvider value={{ projectId, notebookId, resolveProjectFilePath, basePath }}>
      <LexicalComposer initialConfig={editorConfig}>
        <div className={`lexical-editor flex flex-col ${className}`}>
        {showToolbar && (
          <div className="flex-shrink-0">
            <LexicalToolbar 
              config={toolbarConfig} 
              className={toolbarClassName} 
              submitButton={submitButton} 
              cancelButton={cancelButton} 
              fullScreenButton={fullScreenButton}
              isSourceMode={isSourceMode}
              onToggleSourceMode={() => {
                // Get the ref to call toggleSourceMode
                if (ref && typeof ref === 'object' && 'current' in ref && ref.current) {
                  ref.current.toggleSourceMode();
                }
              }}
              isHtmlPreviewMode={isHtmlContent && !isSourceMode}
            />
          </div>
        )}
        
        <div className="lexical-editor-container relative flex-1 overflow-auto flex flex-col">
          {isSourceMode ? (
            // Source mode: raw markdown/HTML textarea
            <textarea
              ref={textareaRef}
              value={sourceText}
              onChange={handleSourceChange}
              onKeyDown={handleSourceKeyDown}
              placeholder={placeholder}
              readOnly={readOnly}
              autoFocus={autoFocus}
              className="w-full flex-1 px-3 py-2 min-h-0 font-mono text-sm outline-none focus:outline-none resize-none bg-white"
              style={{ fontFamily: 'monospace' }}
              data-tour-id="guide.content.instructions.source"
            />
          ) : isHtmlContent ? (
            // HTML Preview mode: render in iframe (read-only)
            <iframe
              srcDoc={sourceText}
              className="w-full flex-1 border-0"
              sandbox="allow-scripts allow-same-origin allow-popups allow-forms"
              title="HTML Preview"
              style={{ minHeight: '400px' }}
            />
          ) : (
            // WYSIWYG mode: rich text editor
            <>
              <RichTextPlugin
                contentEditable={
                  <ContentEditable 
                    className="lexical-content-editable outline-none px-3 py-2 flex-1 min-h-0 focus:outline-none"
                    data-tour-id="guide.content.instructions.editor"
                    readOnly={readOnly}
                  />
                }
                placeholder={
                  <div className="lexical-placeholder absolute top-2 left-3 text-gray-400 pointer-events-none">
                    {placeholder}
                  </div>
                }
                ErrorBoundary={LexicalErrorBoundary}
              />
              
              <HistoryPlugin />
              {autoFocus && <AutoFocusPlugin />}
              <LinkPlugin />
              <ListPlugin />
              <TablePlugin />
              <MarkdownShortcutPlugin transformers={ALL_TRANSFORMERS} />
            </>
          )}
          
          {/* Expose setValue/getValue methods via ref */}
          <EditorRefPlugin 
            editorRef={ref} 
            isSourceMode={isSourceMode}
            setIsSourceMode={setIsSourceMode}
            sourceText={sourceText}
            setSourceText={setSourceText}
            lastExportedRef={lastExportedRef}
            listenerRef={listenerRef}
          />
          {/* Notify parent when editor is ready */}
          <ReadyPlugin onReady={onReady} />
        </div>
      </div>
      </LexicalComposer>
    </ImageNodeContextProvider>
  );
});

export default LexicalEditor; 