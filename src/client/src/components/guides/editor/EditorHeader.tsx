import { FiDownload, FiLoader, FiSave, FiX } from 'react-icons/fi';
import { TourStartButton } from '../../../tour/TourStartButton';
import { HeaderActionsBar } from '../../common/HeaderActionsBar';
import { GuideAntsGuideButton } from '../../../features/guideantsGuide/GuideAntsGuideButton';
import { HomeButton } from '../../common/HomeButton';
import { SettingsButton } from '../../common/SettingsButton';

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

const headerIconButtonClass =
  'flex h-10 w-10 items-center justify-center rounded-md border border-gray-300 bg-white text-gray-700 transition-colors hover:bg-gray-50';

export function EditorHeader({ isEditing, saving, showExport, entityType, entityName, hasValidationErrors, onCancel, onSave, onExport, tourScreenId }: EditorHeaderProps) {
  const entityLabel = entityType.charAt(0).toUpperCase() + entityType.slice(1);
  const backLabel = entityType === 'guide' ? 'Back to Guides' : 'Back to Assistants';
  const saveLabel = saving ? 'Saving...' : `Save ${entityLabel}`;

  return (
    <div className="bg-white border-b px-8 py-4" data-tour-id="guide.header.container">
      <div className="max-w-7xl mx-auto">
        <div className="flex items-center justify-between gap-2">
          <div className="flex min-w-0 flex-1 items-center gap-4">
            <button
              onClick={onCancel}
              className="text-sm text-gray-600 hover:text-gray-900 flex shrink-0 items-center gap-1"
              data-tour-id="guide.header.back"
            >
              ← {backLabel}
            </button>
            <div className="h-6 w-px shrink-0 bg-gray-300"></div>
            <h1 className="truncate text-xl font-semibold text-gray-900" data-tour-id="guide.header.title">
              {isEditing && entityName ? `Editing ${entityName} ${entityLabel}` : isEditing ? `Edit ${entityLabel}` : `Create ${entityLabel}`}
            </h1>
          </div>
          <HeaderActionsBar>
            <GuideAntsGuideButton />
            {isEditing && showExport && (
              <button
                type="button"
                onClick={onExport}
                className={headerIconButtonClass}
                aria-label="Export"
                title="Export"
                data-tour-id="guide.header.export"
              >
                <FiDownload className="h-4 w-4" />
                <span className="sr-only">Export</span>
              </button>
            )}
            <button
              type="button"
              onClick={onCancel}
              className={headerIconButtonClass}
              aria-label="Cancel"
              title="Cancel"
              data-tour-id="guide.header.cancel"
            >
              <FiX className="h-4 w-4" />
              <span className="sr-only">Cancel</span>
            </button>
            <button
              type="button"
              onClick={onSave}
              disabled={saving || hasValidationErrors}
              className="flex h-10 w-10 items-center justify-center rounded-md bg-blue-600 text-white transition-colors hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50"
              aria-label={saveLabel}
              title={hasValidationErrors ? 'Fix validation errors before saving' : saveLabel}
              data-tour-id="guide.header.save"
            >
              {saving ? (
                <FiLoader className="h-4 w-4 animate-spin" />
              ) : (
                <FiSave className="h-4 w-4" />
              )}
              <span className="sr-only">{saveLabel}</span>
            </button>
            <HomeButton />
            <SettingsButton />
            <div data-tour-id="guide.header.help">
              <TourStartButton screenId={tourScreenId ?? 'guideBuilder'} inline />
            </div>
          </HeaderActionsBar>
        </div>
      </div>
    </div>
  );
}
