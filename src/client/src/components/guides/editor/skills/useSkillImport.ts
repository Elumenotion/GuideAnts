import { useCallback, useState } from 'react';
import type { AssistantSkillSaveDto } from '../../../../types/guides';
import { buildSkillUploadsFromFolder, buildSkillUploadsFromZip } from './skillImportHelpers';

export function useSkillImport() {
  const [error, setError] = useState<string | null>(null);
  const [isImporting, setIsImporting] = useState(false);

  const importFolder = useCallback(async (files: FileList | File[]) => {
    setIsImporting(true);
    setError(null);
    try {
      return await buildSkillUploadsFromFolder(files);
    } catch (importError) {
      const message = importError instanceof Error ? importError.message : 'Failed to import skill folder.';
      setError(message);
      throw importError;
    } finally {
      setIsImporting(false);
    }
  }, []);

  const importZip = useCallback(async (file: File) => {
    setIsImporting(true);
    setError(null);
    try {
      return await buildSkillUploadsFromZip(file);
    } catch (importError) {
      const message = importError instanceof Error ? importError.message : 'Failed to import skill zip.';
      setError(message);
      throw importError;
    } finally {
      setIsImporting(false);
    }
  }, []);

  const clearError = useCallback(() => setError(null), []);

  return {
    error,
    isImporting,
    importFolder,
    importZip,
    clearError,
  };
}

export type ImportedSkillPackage = {
  skill: AssistantSkillSaveDto;
  originalMarkdown: string;
};
