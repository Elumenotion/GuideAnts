import type { HuggingFaceRepositoryFileDto } from '../../../../types/settings';
import type {
  ClassifyFn,
  RoleCandidate,
  RoleClassification,
} from '../common';

export type SdRoleId = 'diffusion' | 'vae' | 'textEncoder';

const WEIGHT_EXTS = /\.(safetensors|gguf|bin|pt|onnx)$/i;
const SHARD_PART_RE = /-(\d{4,5})-of-(\d{4,5})\./i;

const DIFFUSION_HINTS = [/flux/i, /diffusion/i, /unet/i, /transformer/i];
const DIFFUSION_NEGATIVE = [/vae/i, /text_encoder/i, /text-encoder/i, /clip/i, /encoder/i];

const VAE_POSITIVE = [/vae/i, /ae_?decoder/i, /encoder/i];
const VAE_NEGATIVE = [/text[_-]?encoder/i, /clip/i];

const TEXT_ENCODER_HINTS = [/text[_-]?encoder/i, /clip/i, /t5/i, /qwen/i, /llama/i, /llm/i];
const TEXT_ENCODER_NEGATIVE = [/vae/i, /diffusion/i, /unet/i];

function leafName(path: string): string {
  const idx = path.lastIndexOf('/');
  return idx >= 0 ? path.substring(idx + 1) : path;
}

function isSharded(file: HuggingFaceRepositoryFileDto): boolean {
  if (file.sharded) {
    return true;
  }
  return SHARD_PART_RE.test(leafName(file.path));
}

function matchesAny(patterns: RegExp[], leaf: string): boolean {
  return patterns.some((re) => re.test(leaf));
}

function candidateForFile(
  file: HuggingFaceRepositoryFileDto,
  badge: string,
): RoleCandidate {
  const badges = [badge];
  if (file.quantLabel) {
    badges.push(file.quantLabel);
  }
  const sharded = isSharded(file);
  if (sharded && file.shardIndex && file.shardTotal) {
    badges.push(`sharded ${file.shardIndex}/${file.shardTotal}`);
  }
  return {
    path: file.path,
    size: file.size ?? null,
    badges,
    disabled: sharded,
    disabledReason: sharded ? 'sharded weights — not supported' : undefined,
  };
}

interface RoleMatcher {
  positive: RegExp[];
  negative: RegExp[];
  badge: string;
  emptyMessage: string;
}

const MATCHERS: Record<SdRoleId, RoleMatcher> = {
  diffusion: {
    positive: DIFFUSION_HINTS,
    negative: DIFFUSION_NEGATIVE,
    badge: 'diffusion',
    emptyMessage:
      'No recognizable diffusion/unet weight file found. Use manual entry if the repo uses a non-standard layout.',
  },
  vae: {
    positive: VAE_POSITIVE,
    negative: VAE_NEGATIVE,
    badge: 'vae',
    emptyMessage: 'No VAE weights found in this repo.',
  },
  textEncoder: {
    positive: TEXT_ENCODER_HINTS,
    negative: TEXT_ENCODER_NEGATIVE,
    badge: 'text_encoder',
    emptyMessage: 'No CLIP / text encoder / T5 weights found in this repo.',
  },
};

function roleCandidates(
  files: HuggingFaceRepositoryFileDto[],
  matcher: RoleMatcher,
): HuggingFaceRepositoryFileDto[] {
  const matches: HuggingFaceRepositoryFileDto[] = [];
  for (const file of files) {
    const leaf = leafName(file.path);
    if (!WEIGHT_EXTS.test(leaf)) {
      continue;
    }
    if (matchesAny(matcher.negative, leaf)) {
      continue;
    }
    if (!matchesAny(matcher.positive, leaf)) {
      continue;
    }
    matches.push(file);
  }
  return matches;
}

/**
 * Pick the single unambiguous candidate for a role. The plan calls for
 * "auto-select only when there is one defensible choice", so we only pick
 * when exactly one non-sharded candidate exists. If there are multiple
 * viable files (e.g. two VAE variants in the same repo), the operator must
 * choose — the picker leaves the role blank and the submit gate holds.
 */
function pickUnambiguous(
  candidates: HuggingFaceRepositoryFileDto[],
): HuggingFaceRepositoryFileDto | null {
  const nonSharded = candidates.filter((c) => !isSharded(c));
  if (nonSharded.length === 1) {
    return nonSharded[0];
  }
  return null;
}

/**
 * Build a single-role classifier usable with the shared
 * <c>RepositoryFilePicker</c>. Each Image Generation picker is a dedicated
 * instance; this factory stamps out the diffusion/vae/text-encoder
 * classifiers with the same shape.
 */
export function buildSdRoleClassifier(roleId: SdRoleId): ClassifyFn {
  const matcher = MATCHERS[roleId];
  return (files) => {
    const matched = roleCandidates(files, matcher);
    const candidates = matched.map((f) => candidateForFile(f, matcher.badge));
    const auto = pickUnambiguous(matched);
    const result: RoleClassification = {
      candidates,
      autoSelect: auto?.path ?? null,
      emptyMessage: candidates.length === 0 ? matcher.emptyMessage : undefined,
    };
    return { [roleId]: result };
  };
}

export const sdDiffusionClassifier = buildSdRoleClassifier('diffusion');
export const sdVaeClassifier = buildSdRoleClassifier('vae');
export const sdTextEncoderClassifier = buildSdRoleClassifier('textEncoder');
