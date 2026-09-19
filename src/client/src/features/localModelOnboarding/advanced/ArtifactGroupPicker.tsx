import { useMemo, useRef, useState } from 'react';
import { api } from '../../../services/api';
import type { HuggingFaceRepositoryFileDto } from '../../../types/settings';
import { RepositoryFilePicker } from '../../../pages/settings/editors/common';
import type { ClassifyFn } from '../../../pages/settings/editors/common/repositoryPickerTypes';
import { buildGgufArtifactGroups, buildMmprojCandidates, type ArtifactGroup } from '../artifactGroups';
import { formatBytes } from '../curated/format';

const customArtifactClassifier: ClassifyFn = (files) => {
  const groups = buildGgufArtifactGroups(files);
  const mmprojPaths = buildMmprojCandidates(files);
  return {
    'custom.modelGroup': {
      candidates: groups.map((group) => ({
        path: group.id,
        size: group.totalBytes,
        badges: group.sharded ? [`${group.shardCount} shards`, group.label] : [group.label],
      })),
      autoSelect: groups[0]?.id ?? null,
      emptyMessage: groups.length === 0 ? 'No complete GGUF artifact groups found in this repo.' : undefined,
    },
    'custom.mmproj': {
      candidates: mmprojPaths.map((path) => {
        const file = files.find((entry) => entry.path === path);
        return {
          path,
          size: file?.size ?? null,
          badges: ['mmproj'],
        };
      }),
      autoSelect: mmprojPaths[0] ?? null,
      emptyMessage: mmprojPaths.length === 0 ? 'No mmproj file found. Leave blank for text-only models.' : undefined,
    },
  };
};

export interface ArtifactGroupPickerProps {
  repository: string;
  onRepositoryChange: (repository: string) => void;
  resolvedRevision: string;
  onResolvedRevisionChange: (revision: string) => void;
  selectedGroupId: string;
  onSelectedGroupChange: (groupId: string, files: string[]) => void;
  selectedMmproj: string;
  onSelectedMmprojChange: (path: string) => void;
  disabled?: boolean;
}

export function ArtifactGroupPicker({
  repository,
  onRepositoryChange,
  resolvedRevision,
  onResolvedRevisionChange,
  selectedGroupId,
  onSelectedGroupChange,
  selectedMmproj,
  onSelectedMmprojChange,
  disabled = false,
}: ArtifactGroupPickerProps) {
  const [listingFiles, setListingFiles] = useState<HuggingFaceRepositoryFileDto[]>([]);
  const groups = useMemo(() => buildGgufArtifactGroups(listingFiles), [listingFiles]);
  const selectedGroup = groups.find((group) => group.id === selectedGroupId) ?? null;
  // Kept in a ref (not derived from the memo) so the picker's auto-select
  // onChange can resolve the group's files synchronously. During a browse the
  // picker fires onBrowseResolved and its auto-select onChange in the same
  // tick, before a re-render commits listingFiles — reading the stale memo
  // here would resolve to [] and silently clear the selected files.
  const latestFilesRef = useRef<HuggingFaceRepositoryFileDto[]>([]);

  return (
    <div className="space-y-3">
      <RepositoryFilePicker
        repository={repository}
        onRepositoryChange={onRepositoryChange}
        roles={[
          {
            id: 'custom.modelGroup',
            label: 'Model artifact group',
            hint: 'Select a complete quant group including all ordered shards.',
            required: true,
            placeholder: 'Select artifact group…',
          },
          {
            id: 'custom.mmproj',
            label: 'Vision projector (mmproj)',
            hint: 'Optional for text-only models.',
            placeholder: 'Select projector…',
          },
        ]}
        classify={customArtifactClassifier}
        initialValues={{
          'custom.modelGroup': selectedGroupId,
          'custom.mmproj': selectedMmproj,
        }}
        onChange={(values) => {
          const nextGroupId = values['custom.modelGroup'] ?? '';
          const freshGroups = buildGgufArtifactGroups(latestFilesRef.current);
          const group = freshGroups.find((entry) => entry.id === nextGroupId);
          if (nextGroupId !== selectedGroupId) {
            onSelectedGroupChange(nextGroupId, group?.files ?? []);
          }
          onSelectedMmprojChange(values['custom.mmproj'] ?? '');
        }}
        onBrowseResolved={(listing) => {
          latestFilesRef.current = listing.files;
          setListingFiles(listing.files);
          const resolved = listing.resolvedRevision?.trim();
          if (resolved && resolved !== resolvedRevision) {
            onResolvedRevisionChange(resolved);
          }
        }}
        serviceOrigin="llamaCpp"
        repoInputHint='Paste the owner/repo shown at the top of the Hugging Face model page.'
        disabled={disabled}
      />

      <div className="space-y-1">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Resolved revision</label>
        <div className="rounded border border-gray-200 bg-white px-3 py-2 font-mono text-sm text-gray-700">
          {resolvedRevision
            ? `${resolvedRevision.slice(0, 12)}…`
            : 'main'}
        </div>
        <p className="text-[11px] text-gray-500">
          Resolved from the Hugging Face browse; downloads are pinned to this commit.
        </p>
      </div>

      {selectedGroup ? (
        <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-700">
          <div className="font-medium text-gray-900">{selectedGroup.label}</div>
          <div>{formatBytes(selectedGroup.totalBytes)} · {selectedGroup.sharded ? `${selectedGroup.shardCount} shards` : 'single file'}</div>
          <ol className="mt-2 list-decimal pl-4 font-mono">
            {selectedGroup.files.map((file) => (
              <li key={file}>{file}</li>
            ))}
          </ol>
        </div>
      ) : null}
    </div>
  );
}

export function resolveArtifactGroup(groups: ArtifactGroup[], groupId: string): ArtifactGroup | null {
  return groups.find((group) => group.id === groupId) ?? null;
}

export async function browseRepositoryFiles(repository: string) {
  const listing = await api.settings.browseHuggingFaceRepository(repository, { serviceOrigin: 'llamaCpp' });
  return listing.files;
}
