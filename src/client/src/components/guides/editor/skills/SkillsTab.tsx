import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { FaPlus, FaRobot } from 'react-icons/fa';
import type {
  AssistantSkillDto,
  AssistantSkillSaveDto,
  FileDto,
  ToolAssignmentDto,
} from '../../../../types/guides';
import { api } from '../../../../services/api';
import { useToast } from '../../../common/Toast';
import { SkillList } from './SkillList';
import { ImportSkillDialog } from './ImportSkillDialog';
import { AuthorSkillEditor } from './AuthorSkillEditor';
import { CreateFromSkillDialog } from './CreateFromSkillDialog';
import { SkillFilePreviewModal } from './SkillFilePreviewModal';
import { parseSkillFrontmatter } from './skillFrontmatter';
import {
  buildCreateAssistantFromSkillPayload,
  type CreateFromSkillSelection,
} from './createFromSkillHelpers';
import {
  moveSkill,
  nextSkillDisplayOrder,
  reindexSkillDisplayOrders,
} from './skillOrdering';
import { decodePendingFileContent } from './skillFileTreeModel';

interface SkillsTabProps {
  projectId: string;
  assistantId?: string;
  skills: AssistantSkillDto[];
  pendingSkillUploads: AssistantSkillSaveDto[];
  selectedToolTypes: string[];
  hasCodeInterpreterFiles: boolean;
  toolAssignments: ToolAssignmentDto[];
  onSkillsChange: (skills: AssistantSkillDto[]) => void;
  onPendingSkillUploadsChange: (uploads: AssistantSkillSaveDto[]) => void;
  onDownloadFile?: (fileId: string, fileName: string) => void;
}

interface PreviewState {
  skillName: string;
  file: FileDto;
  initialContent?: string;
}

function mergeImportedSkill(
  skill: AssistantSkillSaveDto,
  uploads: AssistantSkillSaveDto[],
): AssistantSkillSaveDto[] {
  const withoutDuplicate = uploads.filter((item) => item.name !== skill.name);
  return [...withoutDuplicate, skill];
}

function syncPendingUploadOrders(
  uploads: AssistantSkillSaveDto[],
  skills: AssistantSkillDto[],
): AssistantSkillSaveDto[] {
  return uploads.map((upload) => {
    const match = skills.find((skill) => skill.name === upload.name);
    return match ? { ...upload, displayOrder: match.displayOrder } : upload;
  });
}

export function SkillsTab({
  projectId,
  assistantId,
  skills,
  pendingSkillUploads,
  selectedToolTypes,
  hasCodeInterpreterFiles,
  toolAssignments,
  onSkillsChange,
  onPendingSkillUploadsChange,
  onDownloadFile,
}: SkillsTabProps) {
  const navigate = useNavigate();
  const { showToast } = useToast();
  const [showImport, setShowImport] = useState(false);
  const [showAuthor, setShowAuthor] = useState(false);
  const [showCreateFromSkill, setShowCreateFromSkill] = useState(false);
  const [createFromSkillInitial, setCreateFromSkillInitial] = useState<string | undefined>();
  const [isCreatingFromSkill, setIsCreatingFromSkill] = useState(false);
  const [createFromSkillError, setCreateFromSkillError] = useState<string | null>(null);
  const [preview, setPreview] = useState<PreviewState | null>(null);

  const availableToolTypes = useMemo(
    () => new Set([...selectedToolTypes, ...toolAssignments.map((tool) => tool.toolType)]),
    [selectedToolTypes, toolAssignments],
  );

  const updateSkills = (nextSkills: AssistantSkillDto[]) => {
    const reindexed = reindexSkillDisplayOrders(nextSkills);
    onSkillsChange(reindexed);
    onPendingSkillUploadsChange(syncPendingUploadOrders(pendingSkillUploads, reindexed));
  };

  const openCreateFromSkill = (primarySkillName?: string) => {
    setCreateFromSkillError(null);
    setCreateFromSkillInitial(primarySkillName);
    setShowCreateFromSkill(true);
  };

  const handleCreateFromSkill = async (selection: CreateFromSkillSelection) => {
    setIsCreatingFromSkill(true);
    setCreateFromSkillError(null);

    try {
      const payload = await buildCreateAssistantFromSkillPayload(
        projectId,
        skills,
        pendingSkillUploads,
        assistantId,
        selection,
      );
      const created = await api.guides.assistants.create(payload);
      showToast({
        type: 'success',
        title: 'Assistant created from skill',
        message: `"${created.name}" is ready to use.`,
      });
      setShowCreateFromSkill(false);
      navigate(`/projects/${projectId}/guides/assistant/${created.id}?tab=general`);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to create assistant from skill.';
      setCreateFromSkillError(message);
      showToast({ type: 'error', title: 'Create assistant failed', message });
    } finally {
      setIsCreatingFromSkill(false);
    }
  };

  const handleImported = (skill: AssistantSkillSaveDto) => {
    const displayOrder = nextSkillDisplayOrder(skills);
    const skillWithOrder = { ...skill, displayOrder };
    onPendingSkillUploadsChange(mergeImportedSkill(skillWithOrder, pendingSkillUploads));

    const manifest = skill.filesToAdd?.find((file) => file.relativePath.endsWith('/SKILL.md'));
    let requiresToolsets: string[] = [];
    let requiresTools: string[] = [];
    let fallbackForToolsets: string[] = [];
    let fallbackForTools: string[] = [];
    if (manifest?.contentBytes) {
      try {
        const markdown = decodePendingFileContent(manifest.contentBytes);
        const parsed = parseSkillFrontmatter(markdown);
        requiresToolsets = parsed.frontmatter.requiresToolsets;
        requiresTools = parsed.frontmatter.requiresTools;
        fallbackForToolsets = parsed.frontmatter.fallbackForToolsets;
        fallbackForTools = parsed.frontmatter.fallbackForTools;
      } catch {
        // display without gating metadata if parse fails
      }
    }

    const displaySkill: AssistantSkillDto = {
      name: skill.name,
      description: skill.description,
      enabled: skill.enabled,
      displayOrder,
      source: skill.source,
      requiresToolsets,
      requiresTools,
      fallbackForToolsets,
      fallbackForTools,
      files: (skill.filesToAdd ?? []).map((file, index) => ({
        id: `pending-${skill.name}-${index}`,
        folderKind: file.folderKind,
        relativePath: file.relativePath,
        created: new Date().toISOString(),
      })),
    };

    updateSkills([...skills.filter((item) => item.name !== skill.name), displaySkill]);
  };

  const handlePreviewFile = (skillName: string, file: FileDto) => {
    if (file.id.startsWith('pending-')) {
      const upload = pendingSkillUploads.find((item) => item.name === skillName);
      const pendingFile = upload?.filesToAdd?.find((item) => item.relativePath === file.relativePath);
      if (pendingFile?.contentBytes) {
        setPreview({
          skillName,
          file,
          initialContent: decodePendingFileContent(pendingFile.contentBytes),
        });
        return;
      }
    }

    setPreview({ skillName, file });
  };

  return (
    <div className="space-y-6" data-tour-id="guide.skills.section">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">Skills</h2>
          <p className="text-sm text-gray-600">
            Import or author SKILL.md packages. Skills are offered to the model when prerequisites are met.
          </p>
          {skills.length > 1 && (
            <p className="mt-1 text-xs text-gray-500">
              Discovery order controls how skills are listed in the model prompt when multiple skills are available.
            </p>
          )}
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => setShowImport(true)}
            className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700"
          >
            <FaPlus className="h-3 w-3" />
            Import SKILL.md
          </button>
          <button
            type="button"
            onClick={() => setShowAuthor(true)}
            className="inline-flex items-center gap-2 rounded-md border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
          >
            Author skill
          </button>
          {skills.length > 0 && (
            <button
              type="button"
              onClick={() => openCreateFromSkill()}
              className="inline-flex items-center gap-2 rounded-md border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
            >
              <FaRobot className="h-3 w-3" />
              Create assistant from skill(s)
            </button>
          )}
        </div>
      </div>

      {createFromSkillError && (
        <div className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700" role="alert">
          {createFromSkillError}
        </div>
      )}

      {skills.length === 0 ? (
        <div className="rounded-lg border border-dashed border-blue-200 bg-white p-8 text-center">
          <p className="text-sm text-gray-600">
            No skills yet. Import a SKILL.md package or author a new skill to get started.
          </p>
        </div>
      ) : (
        <SkillList
          skills={skills}
          availableToolTypes={availableToolTypes}
          hasCodeInterpreterFiles={hasCodeInterpreterFiles}
          onSkillChange={(skillName, skill) => {
            updateSkills(skills.map((item) => (item.name === skillName ? skill : item)));
          }}
          onSkillRemove={(skillName) => {
            updateSkills(skills.filter((skill) => skill.name !== skillName));
            onPendingSkillUploadsChange(
              pendingSkillUploads.filter((item) => item.name !== skillName),
            );
          }}
          onMoveSkill={(skillName, direction) => {
            updateSkills(moveSkill(skills, skillName, direction));
          }}
          onPreviewFile={handlePreviewFile}
          onDownloadFile={onDownloadFile}
          onCreateAssistantFromSkill={openCreateFromSkill}
        />
      )}

      {preview && (
        <SkillFilePreviewModal
          isOpen
          onClose={() => setPreview(null)}
          fileName={preview.file.relativePath.split('/').pop() ?? preview.file.relativePath}
          relativePath={preview.file.relativePath}
          initialContent={preview.initialContent}
          assistantId={assistantId}
          fileId={preview.file.id}
        />
      )}

      <ImportSkillDialog
        isOpen={showImport}
        onClose={() => setShowImport(false)}
        onImported={handleImported}
      />
      <AuthorSkillEditor
        isOpen={showAuthor}
        nextDisplayOrder={nextSkillDisplayOrder(skills)}
        onClose={() => setShowAuthor(false)}
        onAuthored={handleImported}
      />
      <CreateFromSkillDialog
        isOpen={showCreateFromSkill}
        initialPrimarySkillName={createFromSkillInitial}
        skills={skills.map((skill) => ({
          name: skill.name,
          description: skill.description,
          requiresToolsets: skill.requiresToolsets,
          requiresTools: skill.requiresTools,
        }))}
        isConfirming={isCreatingFromSkill}
        onConfirm={(selection) => {
          void handleCreateFromSkill(selection);
        }}
        onCancel={() => {
          if (!isCreatingFromSkill) {
            setShowCreateFromSkill(false);
          }
        }}
      />
    </div>
  );
}
