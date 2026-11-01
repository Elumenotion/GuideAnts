/**
 * Bounded label + tooltip for model entries rendered inside a native
 * `<select>` (chat toolbar model override, guide/assistant ModelSelector,
 * settings default-model pickers).
 *
 * A native `<select>` popup sizes itself to its widest option, and the
 * current Chromium build no longer clamps that width — a long Models
 * `Description` row (e.g. the Max AI stack rows with URLs) used to stretch
 * the whole popup off-screen. CSS cannot cap a native popup, so the label
 * itself is bounded to MODEL_OPTION_LABEL_MAX characters; the full
 * displayName + description stays available via the option's `title`
 * (native hover tooltip).
 */
export const MODEL_OPTION_LABEL_MAX = 80;

export interface ModelOptionLabel {
  /** Bounded text to render inside the `<option>`. */
  label: string;
  /** Full, untruncated text for the `<option title>` tooltip. */
  title: string;
}

export function modelOptionLabel(
  displayName: string,
  description?: string | null,
  separator = ' — ',
): ModelOptionLabel {
  const full = description
    ? `${displayName}${separator}${description}`
    : displayName;
  let label = full;
  if (full.length > MODEL_OPTION_LABEL_MAX) {
    label = `${full.slice(0, MODEL_OPTION_LABEL_MAX - 1)}…`;
  }
  return { label, title: full };
}
