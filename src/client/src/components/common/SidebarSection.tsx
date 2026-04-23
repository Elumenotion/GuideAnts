import React, { useRef } from 'react';

interface SidebarSectionProps<T = string> {
  title: string;
  type: T;
  isExpanded: boolean;
  onToggle: () => void;
  onSelect: (type: T, id: string) => void;
  selectedItem: any | null;
  items: Array<{ id: string; label: string; }>;
  onAdd?: () => void;
  onUploadFiles?: (files: FileList) => void;
  onOpenUploadDialog?: () => void;
  onCreateFolder?: () => void;
  disabled?: boolean;
  showAddButton?: boolean;
  headerActions?: React.ReactNode;
  children?: React.ReactNode;
  noIndentChildren?: boolean;
  'data-tour-id'?: string;
}

export function SidebarSection<T = string>({ 
  title, 
  type, 
  isExpanded, 
  onToggle, 
  onSelect, 
  selectedItem, 
  items, 
  onAdd,
  onUploadFiles,
  onOpenUploadDialog,
  disabled = false,
  showAddButton = false,
  headerActions,
  children,
  noIndentChildren = false,
  'data-tour-id': dataTourId
}: SidebarSectionProps<T>) {
  const fileInputRef = useRef<HTMLInputElement>(null);
  const showUpload = (type === 'contentFiles' || type === 'notebookFiles') && (Boolean(onOpenUploadDialog) || Boolean(onUploadFiles));

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files && onUploadFiles) {
      onUploadFiles(e.target.files);
      // Reset the input so the same file can be selected again
      e.target.value = '';
    }
  };

  return (
    <div className="mb-4" data-tour-id={dataTourId}>
      <div className="flex items-center p-2">
        <span className="flex-1 font-medium">{title}</span>
        <div className="flex items-center">
          {headerActions}
          {showUpload && (
            <>
              <button
                onClick={() => {
                  if (onOpenUploadDialog) {
                    onOpenUploadDialog();
                  } else {
                    fileInputRef.current?.click();
                  }
                }}
                className="p-1.5 text-white bg-blue-600 hover:bg-blue-700 rounded-full flex items-center justify-center disabled:opacity-50 disabled:cursor-not-allowed"
                title="Upload files"
                disabled={disabled}
              >
                <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
                  <polyline points="17 8 12 3 7 8"></polyline>
                  <line x1="12" y1="3" x2="12" y2="15"></line>
                </svg>
              </button>
              {/* Hidden file input retained for fallback/manual trigger if needed */}
              <input
                type="file"
                ref={fileInputRef}
                onChange={handleFileChange}
                className="hidden"
                multiple
                accept="*/*"
              />
            </>
          )}
          {(type === 'links' || showAddButton) && (
            <button
              onClick={onAdd}
              className="p-1.5 text-white bg-blue-600 hover:bg-blue-700 rounded-full flex items-center justify-center disabled:opacity-50 disabled:cursor-not-allowed"
              title={`Add new ${type === 'projectMembers' ? 'project member' : 'link'}`}
              disabled={disabled}
            >
              <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <line x1="12" y1="5" x2="12" y2="19"></line>
                <line x1="5" y1="12" x2="19" y2="12"></line>
              </svg>
            </button>
          )}
          <button
            onClick={onToggle}
            className="ml-2 text-gray-500 disabled:opacity-50 disabled:cursor-not-allowed"
            aria-label={isExpanded ? "Collapse section" : "Expand section"}
            disabled={disabled}
          >
            {isExpanded ? (
              <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <polyline points="6 9 12 15 18 9"></polyline>
              </svg>
            ) : (
              <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <polyline points="9 18 15 12 9 6"></polyline>
              </svg>
            )}
          </button>
        </div>
      </div>
      {isExpanded && (
        <div className={noIndentChildren ? '' : 'pl-4'}>
          {children ? children : (
            <>
              {items.map(item => (
                <button
                  key={item.id}
                  onClick={() => onSelect(type, item.id)}
                  className={`w-full text-left py-1 px-2 text-sm ${
                    selectedItem?.type === type && selectedItem.id === item.id
                      ? 'text-blue-600'
                      : ''
                  } disabled:opacity-50 disabled:cursor-not-allowed`}
                  disabled={disabled}
                >
                  {item.label}
                </button>
              ))}
            </>
          )}
        </div>
      )}
    </div>
  );
} 