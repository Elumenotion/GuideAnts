import { useState } from 'react';
import { FaArrowDown, FaArrowUp, FaRobot, FaTimes, FaBook } from 'react-icons/fa';
import type { FileDto } from '../../../../types/guides';
import { buildSkillCardViewModel } from './skillCardViewModel';
import { computeSkillGating } from './skillGating';
import { SkillFileTree } from './SkillFileTree';

interface SkillCardProps {
  skill: {
    name: string;
    description: string;
    enabled: boolean;
    displayOrder: number;
    source: import('../../../../types/guides').SkillSourceKind;
    requiresToolsets: string[];
    requiresTools: string[];
    fallbackForToolsets: string[];
    fallbackForTools: string[];
    files: FileDto[];
  };
  availableToolTypes: Set<string>;
  hasCodeInterpreterFiles: boolean;
  canMoveUp: boolean;
  canMoveDown: boolean;
  showOrdering: boolean;
  discoveryOrder: number;
  onEnabledChange: (enabled: boolean) => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
  onRemove: () => void;
  onPreviewFile: (file: FileDto) => void;
  onDownloadFile?: (fileId: string, fileName: string) => void;
  onCreateAssistant?: () => void;
}

export function SkillCard({
  skill,
  availableToolTypes,
  hasCodeInterpreterFiles,
  canMoveUp,
  canMoveDown,
  showOrdering,
  discoveryOrder,
  onEnabledChange,
  onMoveUp,
  onMoveDown,
  onRemove,
  onPreviewFile,
  onDownloadFile,
  onCreateAssistant,
}: SkillCardProps) {
  const [filesExpanded, setFilesExpanded] = useState(false);
  const gating = computeSkillGating(
    {
      requiresToolsets: skill.requiresToolsets,
      requiresTools: skill.requiresTools,
      fallbackForToolsets: skill.fallbackForToolsets,
      fallbackForTools: skill.fallbackForTools,
    },
    availableToolTypes,
    hasCodeInterpreterFiles,
  );
  const viewModel = buildSkillCardViewModel(skill.source, gating, skill.files);

  return (
    <article
      className="rounded-lg border border-blue-200 bg-blue-50/60 shadow-sm"
      aria-label={`Skill ${skill.name}`}
    >
      <div className="space-y-3 p-4">
        <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
          <div className="min-w-0 flex-1 space-y-2">
            <div className="flex flex-wrap items-center gap-2">
              <FaBook className="h-4 w-4 text-blue-700" />
              <h3 className="text-base font-semibold text-gray-900">{skill.name}</h3>
              <span className={`inline-flex rounded px-2 py-0.5 text-xs font-medium ${viewModel.sourceClassName}`}>
                {skill.source}
              </span>
              <span className={`inline-flex rounded px-2 py-0.5 text-xs font-medium ${viewModel.gatingClassName}`}>
                {gating.statusLabel}
              </span>
            </div>
            <p className="text-sm text-gray-700">{skill.description}</p>
            <p className="text-xs text-gray-600" role="status" aria-live="polite">
              {viewModel.gatingSummary}
            </p>
          </div>

          <div className="flex flex-wrap items-center gap-3 lg:justify-end">
            <label className="inline-flex items-center gap-2 text-sm text-gray-700">
              <input
                type="checkbox"
                checked={skill.enabled}
                onChange={(event) => onEnabledChange(event.target.checked)}
                className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              />
              Enabled
            </label>

            {showOrdering && (
              <div
                className="flex items-center gap-1"
                title="Controls the order skills appear in the model discovery block at inference time."
              >
                <span className="text-xs text-gray-500">Discovery order</span>
                <button
                  type="button"
                  aria-label={`Move ${skill.name} earlier in discovery order`}
                  disabled={!canMoveUp}
                  onClick={onMoveUp}
                  className="rounded border border-blue-200 bg-white p-1 text-gray-600 hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <FaArrowUp className="h-3 w-3" />
                </button>
                <span className="w-6 text-center text-sm font-medium text-gray-800">
                  {discoveryOrder}
                </span>
                <button
                  type="button"
                  aria-label={`Move ${skill.name} later in discovery order`}
                  disabled={!canMoveDown}
                  onClick={onMoveDown}
                  className="rounded border border-blue-200 bg-white p-1 text-gray-600 hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <FaArrowDown className="h-3 w-3" />
                </button>
              </div>
            )}

            {onCreateAssistant && (
              <button
                type="button"
                onClick={onCreateAssistant}
                className="inline-flex items-center gap-1 rounded-md border border-blue-200 bg-white px-2 py-1 text-sm text-blue-700 hover:bg-blue-100"
              >
                <FaRobot className="h-3 w-3" />
                Create assistant
              </button>
            )}

            <button
              type="button"
              onClick={onRemove}
              className="inline-flex items-center gap-1 rounded-md border border-red-200 bg-white px-2 py-1 text-sm text-red-600 hover:bg-red-50"
            >
              <FaTimes className="h-3 w-3" />
              Remove
            </button>
          </div>
        </div>

        <div>
          <button
            type="button"
            onClick={() => setFilesExpanded((value) => !value)}
            className="text-sm font-medium text-blue-700 hover:text-blue-800"
            aria-expanded={filesExpanded}
          >
            {filesExpanded ? 'Hide package files' : `Show package files (${skill.files.length})`}
          </button>
          {filesExpanded && (
            <div className="mt-2">
              <SkillFileTree
                skillName={skill.name}
                files={skill.files}
                onPreviewFile={onPreviewFile}
                onDownloadFile={onDownloadFile}
              />
            </div>
          )}
        </div>
      </div>
    </article>
  );
}
