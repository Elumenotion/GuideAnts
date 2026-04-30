type LocalModelArtifactCandidate = {
  modelRef?: string;
  activeTokenizer?: boolean;
  activeModel?: boolean;
};

export function isSelectableLocalVoiceModelEntry(entry: LocalModelArtifactCandidate): boolean {
  const modelRef = (entry.modelRef ?? '').trim();
  if (!modelRef) return false;
  if (modelRef.startsWith('.')) return false;

  // Only hide tokenizer-only rows; model rows can legitimately carry tokenizer metadata.
  if (entry.activeTokenizer === true && entry.activeModel !== true) return false;

  return true;
}
