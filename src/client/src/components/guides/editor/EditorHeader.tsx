interface EditorHeaderProps {
  isEditing: boolean;
  saving: boolean;
  showExport: boolean; // Control whether to show Export button
  entityType: 'guide' | 'assistant'; // For dynamic titles
  entityName?: string; // Name of the entity being edited
  hasValidationErrors?: boolean; // Disable save button when there are validation errors
  onCancel: () => void;
  onSave: () => void;
  onExport: () => void;
  tourScreenId?: string;
}

import { TourStartButton } from '../../../tour/TourStartButton';

export function EditorHeader({ isEditing, saving, showExport, entityType, entityName, hasValidationErrors, onCancel, onSave, onExport, tourScreenId }: EditorHeaderProps) {
  const entityLabel = entityType.charAt(0).toUpperCase() + entityType.slice(1);
  const backLabel = entityType === 'guide' ? 'Back to Guides' : 'Back to Assistants';
  
  return (
    <div className="bg-white border-b px-8 py-4" data-tour-id="guide.header.container">
      <div className="max-w-7xl mx-auto">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-4">
            <button
              onClick={onCancel}
              className="text-sm text-gray-600 hover:text-gray-900 flex items-center gap-1"
              data-tour-id="guide.header.back"
            >
              ← {backLabel}
            </button>
            <div className="h-6 w-px bg-gray-300"></div>
            <h1 className="text-xl font-semibold text-gray-900" data-tour-id="guide.header.title">
              {isEditing && entityName ? `Editing ${entityName} ${entityLabel}` : isEditing ? `Edit ${entityLabel}` : `Create ${entityLabel}`}
            </h1>
          </div>
          <div className="flex gap-2">
            {isEditing && showExport && (
              <button
                onClick={onExport}
                className="px-3 py-1 text-sm text-gray-700 bg-white border border-gray-300 rounded hover:bg-gray-50 transition-colors"
                data-tour-id="guide.header.export"
              >
                Export
              </button>
            )}
            <button
              onClick={onCancel}
              className="px-3 py-1 text-sm text-gray-700 bg-white border border-gray-300 rounded hover:bg-gray-50 transition-colors"
              data-tour-id="guide.header.cancel"
            >
              Cancel
            </button>
            <button
              onClick={onSave}
              disabled={saving || hasValidationErrors}
              className="px-3 py-1 text-sm text-white bg-blue-600 rounded hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
              title={hasValidationErrors ? 'Fix validation errors before saving' : undefined}
              data-tour-id="guide.header.save"
            >
              {saving ? 'Saving...' : `Save ${entityLabel}`}
            </button>
            {tourScreenId && (
              <div data-tour-id="guide.header.help">
                <TourStartButton screenId={tourScreenId} inline />
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

