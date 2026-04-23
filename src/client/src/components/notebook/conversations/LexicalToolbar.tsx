import { useCallback, useState, useEffect, useRef } from 'react';
import { useLexicalComposerContext } from '@lexical/react/LexicalComposerContext';
import { FaExpandAlt } from 'react-icons/fa';
import { 
  $getSelection, 
  $isRangeSelection,
  FORMAT_TEXT_COMMAND,
  TextFormatType,
  $createTextNode,
  $createParagraphNode,
} from 'lexical';
import { 
  INSERT_UNORDERED_LIST_COMMAND,
  INSERT_ORDERED_LIST_COMMAND,
  REMOVE_LIST_COMMAND,
  $isListNode,
} from '@lexical/list';
import { $findMatchingParent } from '@lexical/utils';
import { INSERT_TABLE_COMMAND } from '@lexical/table';
import { $createHeadingNode, $createQuoteNode, HeadingTagType, $isHeadingNode, $isQuoteNode } from '@lexical/rich-text';
import { $createLinkNode, $isLinkNode } from '@lexical/link';
import { $setBlocksType } from '@lexical/selection';
import { $createImageNode } from './ImageNode';
import { $createAudioNode } from './AudioNode';
import { $createVideoNode } from './VideoNode';
import { ToolbarConfig } from './LexicalEditor';

interface LexicalToolbarProps {
  config: ToolbarConfig;
  className?: string;
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
  isSourceMode?: boolean;
  onToggleSourceMode?: () => void;
  /** When true, content is HTML - only show source toggle button */
  isHtmlPreviewMode?: boolean;
}

// Extract dialog components outside to prevent remounting on parent re-renders
interface LinkDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onInsert: (url: string, text: string) => void;
}

function LinkDialog({ isOpen, onClose, onInsert }: LinkDialogProps) {
  const [url, setUrl] = useState('');
  const [text, setText] = useState('');

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (url && text) {
      onInsert(url, text);
      setUrl('');
      setText('');
    }
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="bg-white p-6 rounded-lg shadow-xl max-w-md w-full mx-4">
        <h3 className="text-lg font-semibold mb-4">Insert Link</h3>
        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="link-text" className="block text-sm font-medium text-gray-700 mb-1">
              Link Text
            </label>
            <input
              id="link-text"
              type="text"
              value={text}
              onChange={(e) => setText(e.target.value)}
              placeholder="Enter link text"
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              autoFocus
            />
          </div>
          <div>
            <label htmlFor="link-url" className="block text-sm font-medium text-gray-700 mb-1">
              URL
            </label>
            <input
              id="link-url"
              type="text"
              inputMode="url"
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              placeholder="https://example.com"
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div className="flex gap-2 justify-end">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 text-gray-600 border border-gray-300 rounded hover:bg-gray-50"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={!url || !text}
              className="px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Insert Link
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

interface ImageDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onInsert: (url: string, altText: string) => void;
}

function ImageDialog({ isOpen, onClose, onInsert }: ImageDialogProps) {
  const [url, setUrl] = useState('');
  const [altText, setAltText] = useState('');

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (url && altText) {
      onInsert(url, altText);
      setUrl('');
      setAltText('');
    }
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="bg-white p-6 rounded-lg shadow-xl max-w-md w-full mx-4">
        <h3 className="text-lg font-semibold mb-4">Insert Image</h3>
        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="image-alt" className="block text-sm font-medium text-gray-700 mb-1">
              Alt Text
            </label>
            <input
              id="image-alt"
              type="text"
              value={altText}
              onChange={(e) => setAltText(e.target.value)}
              placeholder="Describe the image"
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              autoFocus
            />
          </div>
          <div>
            <label htmlFor="image-url" className="block text-sm font-medium text-gray-700 mb-1">
              Image URL
            </label>
            <input
              id="image-url"
              type="text"
              inputMode="url"
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              placeholder="https://example.com/image.jpg"
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div className="flex gap-2 justify-end">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 text-gray-600 border border-gray-300 rounded hover:bg-gray-50"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={!url || !altText}
              className="px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Insert Image
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

interface AudioDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onInsert: (url: string) => void;
}

function AudioDialog({ isOpen, onClose, onInsert }: AudioDialogProps) {
  const [url, setUrl] = useState('');
  
  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (url) {
      onInsert(url);
      setUrl('');
    }
  };
  
  if (!isOpen) return null;
  
  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="bg-white p-6 rounded-lg shadow-xl max-w-md w-full mx-4">
        <h3 className="text-lg font-semibold mb-4">Insert Audio</h3>
        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="audio-url" className="block text-sm font-medium text-gray-700 mb-1">
              Audio URL
            </label>
            <input
              id="audio-url"
              type="text"
              inputMode="url"
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              placeholder="./Output/audio.mp3"
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              autoFocus
            />
          </div>
          <div className="flex gap-2 justify-end">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 text-gray-600 border border-gray-300 rounded hover:bg-gray-50"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={!url}
              className="px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Insert Audio
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

interface VideoDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onInsert: (url: string) => void;
}

function VideoDialog({ isOpen, onClose, onInsert }: VideoDialogProps) {
  const [url, setUrl] = useState('');
  
  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (url) {
      onInsert(url);
      setUrl('');
    }
  };
  
  if (!isOpen) return null;
  
  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="bg-white p-6 rounded-lg shadow-xl max-w-md w-full mx-4">
        <h3 className="text-lg font-semibold mb-4">Insert Video</h3>
        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="video-url" className="block text-sm font-medium text-gray-700 mb-1">
              Video URL
            </label>
            <input
              id="video-url"
              type="text"
              inputMode="url"
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              placeholder="./Output/video.mp4"
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              autoFocus
            />
          </div>
          <div className="flex gap-2 justify-end">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 text-gray-600 border border-gray-300 rounded hover:bg-gray-50"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={!url}
              className="px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Insert Video
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

interface TablePickerProps {
  isOpen: boolean;
  onClose: () => void;
  onInsert: (rows: number, cols: number, includeHeaders: boolean) => void;
  anchorRef: React.RefObject<HTMLButtonElement | null>;
}

function TablePicker({ isOpen, onClose, onInsert, anchorRef }: TablePickerProps) {
  const [hoverRow, setHoverRow] = useState(0);
  const [hoverCol, setHoverCol] = useState(0);
  const [includeHeaders, setIncludeHeaders] = useState(true);
  const [showCustomDialog, setShowCustomDialog] = useState(false);
  const [customRows, setCustomRows] = useState('4');
  const [customCols, setCustomCols] = useState('4');
  const pickerRef = useRef<HTMLDivElement>(null);

  const MAX_ROWS = 6;
  const MAX_COLS = 8;

  // Close picker when clicking outside
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (pickerRef.current && !pickerRef.current.contains(event.target as Node) &&
          anchorRef.current && !anchorRef.current.contains(event.target as Node)) {
        onClose();
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      return () => document.removeEventListener('mousedown', handleClickOutside);
    }
  }, [isOpen, onClose, anchorRef]);

  const handleGridClick = () => {
    if (hoverRow > 0 && hoverCol > 0) {
      onInsert(hoverRow, hoverCol, includeHeaders);
      onClose();
      setHoverRow(0);
      setHoverCol(0);
    }
  };

  const handleCustomSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    const rows = parseInt(customRows, 10);
    const cols = parseInt(customCols, 10);
    if (rows > 0 && cols > 0 && rows <= 50 && cols <= 20) {
      onInsert(rows, cols, includeHeaders);
      onClose();
      setShowCustomDialog(false);
      setCustomRows('4');
      setCustomCols('4');
    }
  };

  if (!isOpen) return null;

  if (showCustomDialog) {
    return (
      <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
        <div className="bg-white p-6 rounded-lg shadow-xl max-w-sm w-full mx-4">
          <h3 className="text-lg font-semibold mb-4">Custom Table Size</h3>
          <form onSubmit={handleCustomSubmit} className="space-y-4">
            <div className="flex gap-4">
              <div className="flex-1">
                <label htmlFor="custom-rows" className="block text-sm font-medium text-gray-700 mb-1">
                  Rows
                </label>
                <input
                  id="custom-rows"
                  type="number"
                  min="1"
                  max="50"
                  value={customRows}
                  onChange={(e) => setCustomRows(e.target.value)}
                  className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                  autoFocus
                />
              </div>
              <div className="flex-1">
                <label htmlFor="custom-cols" className="block text-sm font-medium text-gray-700 mb-1">
                  Columns
                </label>
                <input
                  id="custom-cols"
                  type="number"
                  min="1"
                  max="20"
                  value={customCols}
                  onChange={(e) => setCustomCols(e.target.value)}
                  className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
            </div>
            <label className="flex items-center gap-2 text-sm text-gray-700">
              <input
                type="checkbox"
                checked={includeHeaders}
                onChange={(e) => setIncludeHeaders(e.target.checked)}
                className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              />
              Include header row
            </label>
            <div className="flex gap-2 justify-end">
              <button
                type="button"
                onClick={() => setShowCustomDialog(false)}
                className="px-4 py-2 text-gray-600 border border-gray-300 rounded hover:bg-gray-50"
              >
                Back
              </button>
              <button
                type="submit"
                className="px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600"
              >
                Insert Table
              </button>
            </div>
          </form>
        </div>
      </div>
    );
  }

  return (
    <div 
      ref={pickerRef}
      className="absolute top-full left-0 mt-1 bg-white border border-gray-300 rounded-lg shadow-lg z-50 p-3"
    >
      {/* Grid picker */}
      <div 
        className="grid gap-1 mb-2"
        style={{ gridTemplateColumns: `repeat(${MAX_COLS}, 1fr)` }}
        onMouseLeave={() => { setHoverRow(0); setHoverCol(0); }}
      >
        {Array.from({ length: MAX_ROWS * MAX_COLS }).map((_, index) => {
          const row = Math.floor(index / MAX_COLS) + 1;
          const col = (index % MAX_COLS) + 1;
          const isHighlighted = row <= hoverRow && col <= hoverCol;
          
          return (
            <div
              key={index}
              className={`w-5 h-5 border rounded-sm cursor-pointer transition-colors ${
                isHighlighted 
                  ? 'bg-blue-500 border-blue-600' 
                  : 'bg-gray-100 border-gray-300 hover:border-gray-400'
              }`}
              onMouseEnter={() => { setHoverRow(row); setHoverCol(col); }}
              onClick={handleGridClick}
            />
          );
        })}
      </div>

      {/* Size indicator */}
      <div className="text-center text-sm text-gray-600 mb-2 h-5">
        {hoverRow > 0 && hoverCol > 0 ? `${hoverCol} × ${hoverRow} table` : 'Select table size'}
      </div>

      {/* Header toggle */}
      <label className="flex items-center gap-2 text-sm text-gray-700 py-2 border-t border-gray-200">
        <input
          type="checkbox"
          checked={includeHeaders}
          onChange={(e) => setIncludeHeaders(e.target.checked)}
          className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
        />
        Include header row
      </label>

      {/* Custom size option */}
      <button
        onClick={() => setShowCustomDialog(true)}
        className="w-full text-left text-sm text-gray-600 hover:text-gray-900 hover:bg-gray-50 py-2 px-1 border-t border-gray-200"
      >
        Custom size...
      </button>
    </div>
  );
}

type BlockType = 'paragraph' | 'h1' | 'h2' | 'h3' | 'h4' | 'h5' | 'quote' | 'bullet' | 'number';

export default function LexicalToolbar({ 
  config, 
  className = '', 
  submitButton, 
  cancelButton, 
  fullScreenButton,
  isSourceMode = false,
  onToggleSourceMode,
  isHtmlPreviewMode = false,
}: LexicalToolbarProps) {
  const [editor] = useLexicalComposerContext();
  const [isHeaderDropdownOpen, setIsHeaderDropdownOpen] = useState(false);
  const [isLinkDialogOpen, setIsLinkDialogOpen] = useState(false);
  const [isImageDialogOpen, setIsImageDialogOpen] = useState(false);
  const [isAudioDialogOpen, setIsAudioDialogOpen] = useState(false);
  const [isVideoDialogOpen, setIsVideoDialogOpen] = useState(false);
  const [isTablePickerOpen, setIsTablePickerOpen] = useState(false);
  const tableButtonRef = useRef<HTMLButtonElement>(null);

  // Active format state
  const [isBold, setIsBold] = useState(false);
  const [isItalic, setIsItalic] = useState(false);
  const [isStrikethrough, setIsStrikethrough] = useState(false);
  const [isCode, setIsCode] = useState(false);
  const [isLink, setIsLink] = useState(false);
  const [blockType, setBlockType] = useState<BlockType>('paragraph');

  // Update toolbar state based on selection
  useEffect(() => {
    return editor.registerUpdateListener(({ editorState }) => {
      editorState.read(() => {
        const selection = $getSelection();
        if (!$isRangeSelection(selection)) {
          return;
        }

        // Update text format states
        setIsBold(selection.hasFormat('bold'));
        setIsItalic(selection.hasFormat('italic'));
        setIsStrikethrough(selection.hasFormat('strikethrough'));
        setIsCode(selection.hasFormat('code'));

        // Check if in link
        const node = selection.anchor.getNode();
        const parent = node.getParent();
        setIsLink($isLinkNode(parent) || $isLinkNode(node));

        // Determine block type
        const anchorNode = selection.anchor.getNode();
        const element = anchorNode.getKey() === 'root'
          ? anchorNode
          : $findMatchingParent(anchorNode, (e) => {
              const parent = e.getParent();
              return parent !== null && parent.getKey() === 'root';
            }) || anchorNode.getTopLevelElementOrThrow();

        if ($isListNode(element)) {
          const listType = element.getListType();
          setBlockType(listType === 'bullet' ? 'bullet' : 'number');
        } else if ($isHeadingNode(element)) {
          const tag = element.getTag();
          setBlockType(tag as BlockType);
        } else if ($isQuoteNode(element)) {
          setBlockType('quote');
        } else {
          setBlockType('paragraph');
        }
      });
    });
  }, [editor]);

  const formatText = useCallback((format: TextFormatType) => {
    editor.dispatchCommand(FORMAT_TEXT_COMMAND, format);
  }, [editor]);

  const formatHeading = useCallback((headingSize: HeadingTagType) => {
    editor.update(() => {
      const selection = $getSelection();
      if ($isRangeSelection(selection)) {
        // Toggle: if already this heading, revert to paragraph
        if (blockType === headingSize) {
          $setBlocksType(selection, () => $createParagraphNode());
        } else {
          $setBlocksType(selection, () => $createHeadingNode(headingSize));
        }
      }
    });
    setIsHeaderDropdownOpen(false); // Close dropdown after selection
  }, [editor, blockType]);

  const formatQuote = useCallback(() => {
    editor.update(() => {
      const selection = $getSelection();
      if ($isRangeSelection(selection)) {
        // Toggle: if already quote, revert to paragraph
        if (blockType === 'quote') {
          $setBlocksType(selection, () => $createParagraphNode());
        } else {
          $setBlocksType(selection, () => $createQuoteNode());
        }
      }
    });
  }, [editor, blockType]);

  const formatList = useCallback((listType: 'bullet' | 'number') => {
    // Toggle behavior: if already in this list type, remove it
    if (blockType === listType) {
      editor.dispatchCommand(REMOVE_LIST_COMMAND, undefined);
    } else {
      // Either insert new list or switch list type
      if (listType === 'bullet') {
        editor.dispatchCommand(INSERT_UNORDERED_LIST_COMMAND, undefined);
      } else {
        editor.dispatchCommand(INSERT_ORDERED_LIST_COMMAND, undefined);
      }
    }
  }, [editor, blockType]);

  const insertTable = useCallback((rows: number, cols: number, includeHeaders: boolean) => {
    editor.dispatchCommand(INSERT_TABLE_COMMAND, {
      columns: String(cols),
      rows: String(rows),
      includeHeaders: includeHeaders,
    });
  }, [editor]);

  const insertLink = useCallback((url: string, text: string) => {
    editor.update(() => {
      const selection = $getSelection();
      if ($isRangeSelection(selection)) {
        const linkNode = $createLinkNode(url);
        linkNode.append($createTextNode(text));
        selection.insertNodes([linkNode]);
      }
    });
    setIsLinkDialogOpen(false);
  }, [editor]);

  const insertImage = useCallback((url: string, altText: string) => {
    editor.update(() => {
      const selection = $getSelection();
      if ($isRangeSelection(selection)) {
        // Create proper image node instead of text
        const imageNode = $createImageNode({
          src: url,
          altText: altText,
        });
        selection.insertNodes([imageNode]);
      }
    });
    setIsImageDialogOpen(false);
  }, [editor]);

  const insertAudio = useCallback((url: string) => {
    editor.update(() => {
      const selection = $getSelection();
      if ($isRangeSelection(selection)) {
        const audioNode = $createAudioNode({ src: url });
        selection.insertNodes([audioNode]);
      }
    });
    setIsAudioDialogOpen(false);
  }, [editor]);

  const insertVideo = useCallback((url: string) => {
    editor.update(() => {
      const selection = $getSelection();
      if ($isRangeSelection(selection)) {
        const videoNode = $createVideoNode({ src: url });
        selection.insertNodes([videoNode]);
      }
    });
    setIsVideoDialogOpen(false);
  }, [editor]);

  // HeaderDropdown with useRef for outside click handling
  const dropdownRef = useRef<HTMLDivElement>(null);
  
  const headerOptions = [
    { tag: 'h1' as HeadingTagType, label: 'Heading 1', className: 'text-2xl font-bold' },
    { tag: 'h2' as HeadingTagType, label: 'Heading 2', className: 'text-xl font-bold' },
    { tag: 'h3' as HeadingTagType, label: 'Heading 3', className: 'text-lg font-bold' },
    { tag: 'h4' as HeadingTagType, label: 'Heading 4', className: 'text-base font-bold' },
    { tag: 'h5' as HeadingTagType, label: 'Heading 5', className: 'text-sm font-bold' },
  ];

  // Close dropdown when clicking outside
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(event.target as Node)) {
        setIsHeaderDropdownOpen(false);
      }
    };

    if (isHeaderDropdownOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
      };
    }
  }, [isHeaderDropdownOpen]);

  return (
    <>
      <div className={`lexical-toolbar flex items-center gap-1 p-1.5 md:p-2 border-b border-gray-200 bg-gray-50 ${className}`} data-tour-id="guide.content.instructions.toolbar">
        {/* When in HTML preview mode (not source), show indicator and hide formatting buttons */}
        {isHtmlPreviewMode && !isSourceMode && (
          <span className="text-sm text-gray-500 px-2">HTML Preview (read-only)</span>
        )}
        
        {/* Header dropdown - first position - hidden in HTML preview mode */}
        {config.heading && !isHtmlPreviewMode && (
          <>
            <div className="relative" ref={dropdownRef}>
              <button
                onClick={() => setIsHeaderDropdownOpen(!isHeaderDropdownOpen)}
                className={`toolbar-button px-2 md:px-3 py-1 rounded hover:bg-gray-200 flex items-center gap-1 disabled:opacity-50 disabled:cursor-not-allowed ${
                  ['h1', 'h2', 'h3', 'h4', 'h5'].includes(blockType) ? 'bg-blue-100 text-blue-700' : ''
                }`}
                title="Headings"
                disabled={isSourceMode}
              >
                {['h1', 'h2', 'h3', 'h4', 'h5'].includes(blockType) ? blockType.toUpperCase() : 'H'} <span className="text-xs">▼</span>
              </button>
              
              {isHeaderDropdownOpen && (
                <div className="absolute top-full left-0 mt-1 bg-white border border-gray-300 rounded shadow-lg z-10 min-w-[160px]">
                  {headerOptions.map(({ tag, label, className }) => (
                    <button
                      key={tag}
                      onClick={() => formatHeading(tag)}
                      className={`w-full text-left px-4 py-3 hover:bg-gray-100 ${className} whitespace-nowrap ${
                        blockType === tag ? 'bg-blue-50 text-blue-700' : ''
                      }`}
                    >
                      {label} {blockType === tag ? '✓' : ''}
                    </button>
                  ))}
                </div>
              )}
            </div>
            <div className="toolbar-divider w-px h-6 bg-gray-300 mx-1 hidden md:block" />
          </>
        )}

        {config.bold && !isHtmlPreviewMode && (
          <button
            onClick={() => formatText('bold')}
            className={`toolbar-button px-2 py-1 rounded hover:bg-gray-200 font-bold disabled:opacity-50 disabled:cursor-not-allowed ${
              isBold ? 'bg-blue-100 text-blue-700' : ''
            }`}
            title="Bold (Ctrl+B)"
            disabled={isSourceMode}
          >
            B
          </button>
        )}
        
        {config.italic && !isHtmlPreviewMode && (
          <button
            onClick={() => formatText('italic')}
            className={`toolbar-button px-2 py-1 rounded hover:bg-gray-200 italic disabled:opacity-50 disabled:cursor-not-allowed ${
              isItalic ? 'bg-blue-100 text-blue-700' : ''
            }`}
            title="Italic (Ctrl+I)"
            disabled={isSourceMode}
          >
            I
          </button>
        )}
        
        {/* Strikethrough - hidden on mobile */}
        {config.strikethrough && !isHtmlPreviewMode && (
          <button
            onClick={() => formatText('strikethrough')}
            className={`hidden md:block toolbar-button px-2 py-1 rounded hover:bg-gray-200 line-through disabled:opacity-50 disabled:cursor-not-allowed ${
              isStrikethrough ? 'bg-blue-100 text-blue-700' : ''
            }`}
            title="Strikethrough"
            disabled={isSourceMode}
          >
            S
          </button>
        )}
        
        {/* Inline Code - hidden on mobile */}
        {config.code && !isHtmlPreviewMode && (
          <button
            onClick={() => formatText('code')}
            className={`hidden md:block toolbar-button px-2 py-1 rounded hover:bg-gray-200 font-mono disabled:opacity-50 disabled:cursor-not-allowed ${
              isCode ? 'bg-blue-100 text-blue-700' : 'bg-gray-100'
            }`}
            title="Inline Code"
            disabled={isSourceMode}
          >
            &lt;/&gt;
          </button>
        )}

        {/* Media buttons section - hidden on mobile */}
        {(config.link || config.image || config.audio || config.video) && (
          <div className="hidden md:block toolbar-divider w-px h-6 bg-gray-300 mx-1" />
        )}

        {config.link && !isHtmlPreviewMode && (
          <button
            onClick={() => setIsLinkDialogOpen(true)}
            className={`hidden md:block toolbar-button px-2 py-1 rounded hover:bg-gray-200 disabled:opacity-50 disabled:cursor-not-allowed ${
              isLink ? 'bg-blue-100' : ''
            }`}
            title="Insert Link"
            disabled={isSourceMode}
          >
            🔗
          </button>
        )}

        {config.image && !isHtmlPreviewMode && (
          <button
            onClick={() => setIsImageDialogOpen(true)}
            className="hidden md:block toolbar-button px-2 py-1 rounded hover:bg-gray-200 disabled:opacity-50 disabled:cursor-not-allowed"
            title="Insert Image"
            disabled={isSourceMode}
          >
            🖼️
          </button>
        )}

        {config.audio && !isHtmlPreviewMode && (
          <button
            onClick={() => setIsAudioDialogOpen(true)}
            className="hidden md:block toolbar-button px-2 py-1 rounded hover:bg-gray-200 disabled:opacity-50 disabled:cursor-not-allowed"
            title="Insert Audio"
            disabled={isSourceMode}
          >
            🎵
          </button>
        )}

        {config.video && !isHtmlPreviewMode && (
          <button
            onClick={() => setIsVideoDialogOpen(true)}
            className="hidden md:block toolbar-button px-2 py-1 rounded hover:bg-gray-200 disabled:opacity-50 disabled:cursor-not-allowed"
            title="Insert Video"
            disabled={isSourceMode}
          >
            🎬
          </button>
        )}

        {/* List buttons section */}
        {(config.unorderedList || config.orderedList) && (
          <div className="toolbar-divider w-px h-6 bg-gray-300 mx-1 hidden md:block" />
        )}

        {config.unorderedList && !isHtmlPreviewMode && (
          <button
            onClick={() => formatList('bullet')}
            className={`toolbar-button px-2 py-1 rounded hover:bg-gray-200 flex items-center gap-1 disabled:opacity-50 disabled:cursor-not-allowed ${
              blockType === 'bullet' ? 'bg-blue-100 text-blue-700' : ''
            }`}
            title="Bullet List"
            disabled={isSourceMode}
          >
            <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
              <circle cx="3" cy="4" r="1.5"/>
              <rect x="6" y="3" width="8" height="2"/>
              <circle cx="3" cy="8" r="1.5"/>
              <rect x="6" y="7" width="8" height="2"/>
              <circle cx="3" cy="12" r="1.5"/>
              <rect x="6" y="11" width="8" height="2"/>
            </svg>
          </button>
        )}

        {config.orderedList && !isHtmlPreviewMode && (
          <button
            onClick={() => formatList('number')}
            className={`toolbar-button px-2 py-1 rounded hover:bg-gray-200 flex items-center gap-1 disabled:opacity-50 disabled:cursor-not-allowed ${
              blockType === 'number' ? 'bg-blue-100 text-blue-700' : ''
            }`}
            title="Numbered List"
            disabled={isSourceMode}
          >
            <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
              <text x="1" y="6" fontSize="8" fontFamily="monospace">1.</text>
              <rect x="6" y="3" width="8" height="2"/>
              <text x="1" y="10" fontSize="8" fontFamily="monospace">2.</text>
              <rect x="6" y="7" width="8" height="2"/>
              <text x="1" y="14" fontSize="8" fontFamily="monospace">3.</text>
              <rect x="6" y="11" width="8" height="2"/>
            </svg>
          </button>
        )}

        {/* Blockquote - hidden on mobile */}
        {config.blockquote && !isHtmlPreviewMode && (
          <div className="hidden md:contents">
            <div className="toolbar-divider w-px h-6 bg-gray-300 mx-1" />
            <button
              onClick={formatQuote}
              className={`toolbar-button px-2 py-1 rounded hover:bg-gray-200 disabled:opacity-50 disabled:cursor-not-allowed ${
                blockType === 'quote' ? 'bg-blue-100 text-blue-700' : ''
              }`}
              title="Quote"
              disabled={isSourceMode}
            >
              "
            </button>
          </div>
        )}

        {/* Table - hidden on mobile */}
        {config.table && !isHtmlPreviewMode && (
          <div className="hidden md:contents">
            <div className="toolbar-divider w-px h-6 bg-gray-300 mx-1" />
            <div className="relative">
              <button
                ref={tableButtonRef}
                onClick={() => setIsTablePickerOpen(!isTablePickerOpen)}
                className={`toolbar-button px-2 py-1 rounded hover:bg-gray-200 disabled:opacity-50 disabled:cursor-not-allowed ${
                  isTablePickerOpen ? 'bg-gray-200' : ''
                }`}
                title="Insert Table"
                disabled={isSourceMode}
              >
                ⊞
              </button>
              <TablePicker
                isOpen={isTablePickerOpen}
                onClose={() => setIsTablePickerOpen(false)}
                onInsert={insertTable}
                anchorRef={tableButtonRef}
              />
            </div>
          </div>
        )}

        {/* Source mode toggle */}
        {onToggleSourceMode && (
          <>
            <div className="toolbar-divider w-px h-6 bg-gray-300 mx-1 hidden md:block" />
            <button
              onClick={onToggleSourceMode}
              className={`toolbar-button px-2 py-1 rounded hover:bg-gray-200 font-mono text-sm ${
                isSourceMode ? 'bg-blue-100 text-blue-700 font-semibold' : ''
              }`}
              title="Toggle Markdown Source (Ctrl+M)"
              data-tour-id="guide.content.instructions.toolbar.sourceToggle"
            >
              {`{ }`}
            </button>
          </>
        )}

        {/* Action buttons at the end of toolbar */}
        {(submitButton || cancelButton || fullScreenButton) && (
          <>
            <div className="flex-1" /> {/* Spacer to push buttons to the right */}
            {fullScreenButton && (
              <button
                onClick={fullScreenButton.onClick}
                className="p-2 hover:bg-gray-200 rounded text-gray-500 hover:text-gray-700 mr-2"
                aria-label="Full screen"
                title="Full screen"
              >
                <FaExpandAlt className="w-4 h-4" />
              </button>
            )}
            {cancelButton && (
              <button
                onClick={cancelButton.onClick}
                className="px-4 py-1.5 text-sm bg-white text-gray-600 border border-gray-300 rounded hover:bg-gray-50 mr-2"
                title="Cancel (Esc)"
              >
                {cancelButton.label}
              </button>
            )}
            {submitButton && (
              <button
                onClick={submitButton.onClick}
                disabled={submitButton.disabled}
                className="px-4 py-1.5 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50 flex items-center gap-2"
                title={`${submitButton.label} (Ctrl+Enter)`}
              >
                {submitButton.isLoading ? (
                  <>
                    <div className="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin"></div>
                    <span>{submitButton.label === 'Send' ? 'Sending...' : 'Saving...'}</span>
                  </>
                ) : (
                  <>
                    <span>{submitButton.label}</span>
                    {submitButton.label === 'Send' && (
                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 19l9 2-9-18-9 18 9-2zm0 0v-8" />
                      </svg>
                    )}
                  </>
                )}
              </button>
            )}
          </>
        )}
      </div>
      
      <LinkDialog 
        isOpen={isLinkDialogOpen} 
        onClose={() => setIsLinkDialogOpen(false)} 
        onInsert={insertLink} 
      />
      <ImageDialog 
        isOpen={isImageDialogOpen} 
        onClose={() => setIsImageDialogOpen(false)} 
        onInsert={insertImage} 
      />
      <AudioDialog 
        isOpen={isAudioDialogOpen} 
        onClose={() => setIsAudioDialogOpen(false)} 
        onInsert={insertAudio} 
      />
      <VideoDialog 
        isOpen={isVideoDialogOpen} 
        onClose={() => setIsVideoDialogOpen(false)} 
        onInsert={insertVideo} 
      />
    </>
  );
} 