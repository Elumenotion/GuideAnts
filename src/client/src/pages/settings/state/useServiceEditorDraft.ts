import { useMemo, useState } from 'react';

export interface ServiceEditorDraftState<TFields extends Record<string, unknown>> {
  activeProviderId: string;
  draftsByProvider: Record<string, Partial<TFields>>;
}

export function createInitialServiceEditorDraft<TFields extends Record<string, unknown>>(
  activeProviderId: string,
  initialDrafts: Record<string, Partial<TFields>> = {}
): ServiceEditorDraftState<TFields> {
  return {
    activeProviderId,
    draftsByProvider: { ...initialDrafts },
  };
}

export function applyDraftPatch<TFields extends Record<string, unknown>>(
  state: ServiceEditorDraftState<TFields>,
  providerId: string,
  patch: Partial<TFields>
): ServiceEditorDraftState<TFields> {
  return {
    ...state,
    draftsByProvider: {
      ...state.draftsByProvider,
      [providerId]: {
        ...(state.draftsByProvider[providerId] ?? {}),
        ...patch,
      },
    },
  };
}

export function switchProviderDraft<TFields extends Record<string, unknown>>(
  state: ServiceEditorDraftState<TFields>,
  providerId: string
): ServiceEditorDraftState<TFields> {
  if (state.activeProviderId === providerId) {
    return state;
  }

  return {
    ...state,
    activeProviderId: providerId,
  };
}

export function useServiceEditorDraft<TFields extends Record<string, unknown>>(
  initialProviderId: string,
  initialDrafts: Record<string, Partial<TFields>> = {}
) {
  const [state, setState] = useState<ServiceEditorDraftState<TFields>>(
    createInitialServiceEditorDraft(initialProviderId, initialDrafts)
  );

  const activeDraft = useMemo(
    () => state.draftsByProvider[state.activeProviderId] ?? {},
    [state.activeProviderId, state.draftsByProvider]
  );

  const switchProvider = (providerId: string): void => {
    setState((previous) => switchProviderDraft(previous, providerId));
  };

  const patchActiveDraft = (patch: Partial<TFields>): void => {
    setState((previous) => applyDraftPatch(previous, previous.activeProviderId, patch));
  };

  const dirtyProvidersExcluding = (providerId: string): string[] =>
    Object.entries(state.draftsByProvider)
      .filter(([id, draft]) => id !== providerId && Object.keys(draft).length > 0)
      .map(([id]) => id);

  return {
    ...state,
    activeDraft,
    switchProvider,
    patchActiveDraft,
    dirtyProvidersExcluding,
    setDraftForProvider: (providerId: string, patch: Partial<TFields>): void =>
      setState((previous) => applyDraftPatch(previous, providerId, patch)),
  };
}
