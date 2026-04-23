export type AzureImageProfileKind = 'flux-family' | 'gpt-image-1.5';

/** Infer profile from configured generation deployment name (tool/runtime validation mirrors these families). */
export function inferAzureImageProfile(deploymentName: string): AzureImageProfileKind {
  const d = deploymentName.toLowerCase();
  if (d.includes('gpt-image') || d.includes('gpt_image') || d.includes('gptimage')) {
    return 'gpt-image-1.5';
  }
  return 'flux-family';
}

export function azureProfileDisplayLabel(kind: AzureImageProfileKind): string {
  return kind === 'gpt-image-1.5' ? 'gpt-image family' : 'Flux-style';
}

export function allowedSizesForAzureProfile(kind: AzureImageProfileKind): readonly string[] {
  return kind === 'gpt-image-1.5'
    ? (['1024x1024', '1024x1536', '1536x1024', 'auto'] as const)
    : (['1024x1024', '1024x1792', '1792x1024'] as const);
}

export const localSdAllowedSizes = ['1024x1024', '1024x1792', '1792x1024'] as const;
