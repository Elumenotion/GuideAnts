import React, { useState } from 'react';
import { FaExpandAlt, FaCompress } from 'react-icons/fa';

interface PreviewContainerProps {
  children: React.ReactNode;
  /** Optional additional className for the inner scrollable area */
  contentClassName?: string;
  /** When true, hides the top banner (Preview / Full-screen buttons) */
  hideHeader?: boolean;
  /** Optional actions to render on the right side of the header (e.g., Edit) */
  headerActions?: React.ReactNode;
  /** When true, only render headerActions while in full-screen mode */
  showHeaderActionsOnlyWhenFull?: boolean;
}

/**
 * Wraps preview content with a banner that includes a full-screen toggle.
 * When expanded, the container becomes a fixed overlay covering the entire app.
 */
export default function PreviewContainer({ children, contentClassName = '', hideHeader = false, headerActions, showHeaderActionsOnlyWhenFull = false }: PreviewContainerProps) {
  const [full, setFull] = useState(false);

  const outerCls = full
    ? 'fixed inset-0 z-50 bg-white flex flex-col shadow-lg'
    : 'w-full flex-1 flex flex-col';

  return (
    <div className={outerCls}>
      {!hideHeader && (
        <div className="w-full bg-gray-100 px-4 py-2 border-b flex items-center justify-between">
          <span className="text-sm font-medium">Preview</span>
          <div className="flex items-center gap-2">
            {!showHeaderActionsOnlyWhenFull ? headerActions : full ? headerActions : null}
            <button
              onClick={() => setFull(!full)}
              className="p-2 rounded hover:bg-gray-200 text-gray-600 hover:text-gray-800"
              aria-label={full ? 'Exit full screen' : 'Full screen'}
              title={full ? 'Exit full screen' : 'Full screen'}
            >
              {full ? <FaCompress className="w-4 h-4" /> : <FaExpandAlt className="w-4 h-4" />}
            </button>
          </div>
        </div>
      )}
      <div className={`flex-1 min-h-0 w-full overflow-auto select-text ${contentClassName}`.trim()}>
        {children}
      </div>
    </div>
  );
} 