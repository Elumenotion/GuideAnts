import { useRef } from 'react';
import { FaFolderOpen, FaFileArchive } from 'react-icons/fa';
import { SKILL_IMPORT_GUIDANCE } from './skillFrontmatterErrors';
import { SkillImportErrorPanel } from './SkillImportErrorPanel';
import { useSkillImport } from './useSkillImport';

interface ImportSkillDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onImported: (result: import('./skillImportHelpers').SkillImportResult) => void;
}

export function ImportSkillDialog({ isOpen, onClose, onImported }: ImportSkillDialogProps) {
  const folderInputRef = useRef<HTMLInputElement>(null);
  const zipInputRef = useRef<HTMLInputElement>(null);
  const {
    error,
    isImporting,
    repairContext,
    importFolder,
    importZip,
    importRepaired,
    clearError,
  } = useSkillImport();

  if (!isOpen) {
    return null;
  }

  const handleFolderChange = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const files = event.target.files;
    if (!files || files.length === 0) {
      return;
    }

    try {
      const result = await importFolder(files);
      onImported(result);
      onClose();
    } catch {
      // error state handled by hook
    } finally {
      event.target.value = '';
    }
  };

  const handleZipChange = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (!file) {
      return;
    }

    try {
      const result = await importZip(file);
      onImported(result);
      onClose();
    } catch {
      // error state handled by hook
    } finally {
      event.target.value = '';
    }
  };

  const handleRepair = async () => {
    if (!repairContext) {
      return;
    }

    try {
      const result = await importRepaired();
      if (!result) {
        return;
      }
      onImported(result);
      onClose();
    } catch {
      // error state handled by hook
    }
  };

  return (
    <div
      className="fixed inset-0 z-50 flex items-end justify-center bg-black/40 p-4 sm:items-center"
      role="dialog"
      aria-modal="true"
      aria-labelledby="import-skill-title"
      onClick={onClose}
    >
      <div
        className="max-h-[90vh] w-full max-w-2xl overflow-auto rounded-lg bg-white p-6 shadow-xl"
        onClick={(event) => event.stopPropagation()}
      >
        <h2 id="import-skill-title" className="text-lg font-semibold text-gray-900">
          Import SKILL.md
        </h2>
        <p className="mt-2 text-sm text-gray-600">
          Import a skill folder or zip containing exactly one <code>SKILL.md</code> plus optional
          references, scripts, and assets.
        </p>

        <ul className="mt-3 list-disc space-y-1 pl-5 text-xs text-gray-600">
          {SKILL_IMPORT_GUIDANCE.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>

        <p className="mt-3 text-xs text-gray-500">
          Format reference:{' '}
          <a
            href="https://agentskills.io/specification"
            target="_blank"
            rel="noreferrer"
            className="text-blue-600 underline hover:text-blue-700"
          >
            agentskills.io specification
          </a>
        </p>

        {error && (
          <SkillImportErrorPanel
            error={error}
            isRepairing={isImporting}
            onRepair={repairContext ? handleRepair : undefined}
          />
        )}

        <div className="mt-5 flex flex-col gap-3 sm:flex-row">
          <button
            type="button"
            disabled={isImporting}
            onClick={() => {
              clearError();
              folderInputRef.current?.click();
            }}
            className="inline-flex items-center justify-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            <FaFolderOpen />
            Choose folder
          </button>
          <button
            type="button"
            disabled={isImporting}
            onClick={() => {
              clearError();
              zipInputRef.current?.click();
            }}
            className="inline-flex items-center justify-center gap-2 rounded-md border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
          >
            <FaFileArchive />
            Choose .zip
          </button>
        </div>

        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-gray-300 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50"
          >
            Cancel
          </button>
        </div>

        <input
          ref={folderInputRef}
          type="file"
          className="hidden"
          multiple
          // @ts-expect-error webkitdirectory is supported in Chromium/Electron
          webkitdirectory=""
          directory=""
          onChange={handleFolderChange}
        />
        <input
          ref={zipInputRef}
          type="file"
          className="hidden"
          accept=".zip,application/zip"
          onChange={handleZipChange}
        />
      </div>
    </div>
  );
}
