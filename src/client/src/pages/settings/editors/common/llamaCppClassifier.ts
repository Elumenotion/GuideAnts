import type { HuggingFaceRepositoryFileDto } from '../../../../types/settings';
import type { ClassifyFn, RoleCandidate, RoleClassification } from './repositoryPickerTypes';

export const LLAMA_MODEL_ROLE_ID = 'llamaCpp.model';
export const LLAMA_MMPROJ_ROLE_ID = 'llamaCpp.mmproj';

const QUANT_PREFERENCE = ['Q5_K_M', 'Q5_K_S', 'Q4_K_M'];

function candidateForFile(file: HuggingFaceRepositoryFileDto): RoleCandidate {
  const badges: string[] = [];
  if (file.quantLabel) {
    badges.push(file.quantLabel);
  }
  if (file.sharded && file.shardIndex && file.shardTotal) {
    badges.push(`sharded ${file.shardIndex}/${file.shardTotal}`);
  }
  return {
    path: file.path,
    size: file.size ?? null,
    badges,
    disabled: file.sharded,
    disabledReason: file.sharded ? 'multi-file quant — not supported' : undefined,
  };
}

function pickPreferredQuant(
  ggufs: HuggingFaceRepositoryFileDto[],
): HuggingFaceRepositoryFileDto | null {
  const nonSharded = ggufs.filter((file) => !file.sharded);
  if (nonSharded.length === 0) {
    return null;
  }
  for (const label of QUANT_PREFERENCE) {
    const match = nonSharded
      .filter((file) => (file.quantLabel ?? '').toUpperCase() === label)
      .sort((a, b) => (b.size ?? 0) - (a.size ?? 0))[0];
    if (match) {
      return match;
    }
  }
  return nonSharded.slice().sort((a, b) => (b.size ?? 0) - (a.size ?? 0))[0] ?? null;
}

/**
 * Classifier preserving the llama-cpp Add Model wizard behavior the
 * original inline picker shipped with. It splits the repository listing
 * into the "Model file (GGUF)" and "Vision projector (mmproj)" dropdowns,
 * auto-selecting the largest preferred-quant GGUF for the model role and
 * the first F16 mmproj when present.
 *
 * The auto-select is only a hint — the shared picker ignores it when the
 * operator has already made a selection so refreshing the listing does
 * not clobber an explicit choice.
 */
export const llamaCppClassifier: ClassifyFn = (files) => {
  const ggufFiles = files.filter((file) => file.category === 'gguf');
  const mmprojFiles = files.filter((file) => file.category === 'mmproj');

  const modelCandidates = ggufFiles.map(candidateForFile);
  const mmprojCandidates = mmprojFiles.map(candidateForFile);

  const preferredModel = pickPreferredQuant(ggufFiles);
  const nonShardedMmproj = mmprojFiles.filter((file) => !file.sharded);
  const preferredMmproj =
    nonShardedMmproj.find((file) => /F16/i.test(file.path))
    ?? nonShardedMmproj[0]
    ?? null;

  const model: RoleClassification = {
    candidates: modelCandidates,
    autoSelect: preferredModel?.path ?? null,
    emptyMessage: modelCandidates.length === 0 ? 'No GGUF files found in this repo.' : undefined,
  };

  const mmproj: RoleClassification = {
    candidates: mmprojCandidates,
    autoSelect: preferredMmproj?.path ?? null,
    emptyMessage:
      mmprojCandidates.length === 0
        ? 'This repo has no mmproj file. That is normal for text-only models.'
        : undefined,
  };

  return {
    [LLAMA_MODEL_ROLE_ID]: model,
    [LLAMA_MMPROJ_ROLE_ID]: mmproj,
  };
};
