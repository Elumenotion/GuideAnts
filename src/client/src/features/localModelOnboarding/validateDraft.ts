import type { LocalModelOnboardingDraft } from './contracts';
import { buildLocalModelOnboardingRequest } from './buildCommand';

export function validateLocalModelOnboardingDraft(draft: LocalModelOnboardingDraft): string[] {
  try {
    buildLocalModelOnboardingRequest(draft, {
      defaultCatalogIsActive: draft.catalogIsActive ?? true,
    });
    return [];
  } catch (error) {
    return [error instanceof Error ? error.message : 'Invalid local model configuration.'];
  }
}
