import type { LocalModelOnboardingDraft } from './contracts';
import { buildLocalModelOnboardingRequest } from './buildCommand';

export function validateLocalModelOnboardingDraft(
  draft: LocalModelOnboardingDraft,
  options?: {
    defaultCatalogModelId?: string;
    defaultCatalogDisplayName?: string;
    defaultTargetDirectory?: string;
  }
): string[] {
  try {
    buildLocalModelOnboardingRequest(draft, {
      defaultCatalogModelId: options?.defaultCatalogModelId,
      defaultCatalogDisplayName: options?.defaultCatalogDisplayName,
      defaultTargetDirectory: options?.defaultTargetDirectory,
    });
    return [];
  } catch (error) {
    return [error instanceof Error ? error.message : 'Invalid local model configuration.'];
  }
}
