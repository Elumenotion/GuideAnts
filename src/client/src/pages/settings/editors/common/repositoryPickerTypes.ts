import type {
  HuggingFaceRepositoryFileDto,
  HuggingFaceRepositoryListingDto,
} from '../../../../types/settings';

/**
 * Specification for one role the Hugging Face repository picker needs to
 * fill. Used by per-file services (llama-cpp, Image Generation) so the
 * shared picker renders one labeled dropdown per role.
 *
 * Roles are purely declarative — the shared picker does NOT bake in any
 * service-specific heuristics. The consumer supplies a {@link ClassifyFn}
 * that turns the raw listing into per-role candidate lists.
 */
export interface RolePickerSpec {
  /** Stable id used as the key for onChange/initialValues records. */
  id: string;
  /** Human-readable label rendered next to the dropdown. */
  label: string;
  /** Optional short hint shown under the dropdown. */
  hint?: string;
  /** If true, an empty selection blocks downstream submit UI. */
  required?: boolean;
  /** Placeholder shown in manual-fallback text inputs. */
  manualPlaceholder?: string;
  /** Empty dropdown option label. Defaults to "Select a file…". */
  placeholder?: string;
}

/** One candidate file rendered as an option inside a role dropdown. */
export interface RoleCandidate {
  path: string;
  size?: number | null;
  /** Extra label fragment appended to the option description. */
  detail?: string | null;
  /** Badges rendered next to the description (e.g. quant label). */
  badges?: string[];
  /** Render the option but do not allow selection. */
  disabled?: boolean;
  /** Reason shown as suffix when {@link disabled} is true. */
  disabledReason?: string;
}

/**
 * Result of classifying the listing for one role. <c>autoSelect</c> is only
 * honored when the current selection for that role is empty — this keeps
 * re-browse / refresh from clobbering explicit operator choices.
 */
export interface RoleClassification {
  /** Candidates in the order they should be rendered in the dropdown. */
  candidates: RoleCandidate[];
  /**
   * Optional path of the candidate that should be auto-selected when the
   * operator has not yet made a choice. Classifiers should only set this
   * when they have a single defensible pick; ambiguous cases return null.
   */
  autoSelect?: string | null;
  /** Shown under the dropdown when <c>candidates</c> is empty. */
  emptyMessage?: string;
}

/** Function signature for per-file classifiers (llama-cpp, SD roles, …). */
export type ClassifyFn = (
  files: HuggingFaceRepositoryFileDto[],
  context: { repository: string; listing: HuggingFaceRepositoryListingDto },
) => Record<string, RoleClassification>;

/** One bucket in the preview-only table (weights / tokenizer / config / other). */
export interface SnapshotPreviewBucket {
  id: string;
  label: string;
  files: Array<{
    path: string;
    size?: number | null;
    /** Additional badges surfaced next to the file row (e.g. "voice"). */
    badges?: string[];
  }>;
}

/**
 * Result of the preview-only classifier for whole-snapshot services
 * (ASR / TTS / Embeddings). The picker renders {@link buckets} as a
 * read-only table, {@link totalSizeBytes} as a sum, and
 * {@link warnings} inline (e.g. ">10 GiB total weights").
 */
export interface SnapshotPreview {
  buckets: SnapshotPreviewBucket[];
  totalSizeBytes?: number;
  warnings?: string[];
}

/** Function signature for preview-only classifiers. */
export type ClassifyPreviewFn = (
  files: HuggingFaceRepositoryFileDto[],
  context: { repository: string; listing: HuggingFaceRepositoryListingDto },
) => SnapshotPreview;
