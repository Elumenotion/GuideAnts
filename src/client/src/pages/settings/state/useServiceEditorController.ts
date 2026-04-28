import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../../../services/api';
import type { ProviderEditorStateDto, ServiceEditorStateDto } from '../../../types/settings';
import { useServiceEditorDraft } from './useServiceEditorDraft';
import { buildSavePayload, hasValidationErrors, validateOperativeProviderFields } from './serviceEditorValidation';

export interface UseServiceEditorControllerResult {
  state: ServiceEditorStateDto | null;
  loading: boolean;
  error: string | null;
  saving: boolean;
  fieldErrors: Record<string, string>;
  draft: ReturnType<typeof useServiceEditorDraft<Record<string, unknown>>>;
  selectedProvider: ProviderEditorStateDto | undefined;
  persistedActiveLabel: string;
  editingProviderLabel: string | null;
  providerOptions: Array<{
    providerId: string;
    displayName: string;
    kind: string;
    hasExplicitMode: boolean;
    connectionConfigured: boolean;
    canActivate: boolean;
    blocker: string | null;
  }>;
  load: () => Promise<void>;
  save: () => Promise<boolean>;
  clearFieldError: (fieldName: string) => void;
}

export function useServiceEditorController(serviceId: string): UseServiceEditorControllerResult {
  const [state, setState] = useState<ServiceEditorStateDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});

  const activeProviderId = state?.activeProviderId ?? '';
  const draft = useServiceEditorDraft<Record<string, unknown>>(activeProviderId || 'none', {});
  const draftRef = useRef(draft);
  draftRef.current = draft;

  const providersById = useMemo(() => {
    if (!state) {
      return new Map<string, ProviderEditorStateDto>();
    }
    return new Map(state.providers.map((p) => [p.providerId, p]));
  }, [state]);

  const selectedProvider = providersById.get(draft.activeProviderId) ?? state?.providers[0];
  const persistedActiveProvider = state ? providersById.get(state.activeProviderId) : undefined;
  const persistedActiveLabel = persistedActiveProvider?.displayName
    ?? (state?.activeProviderId ? state.activeProviderId : 'Not configured');
  const editingDifferentProvider = Boolean(state && draft.activeProviderId !== state.activeProviderId);
  const editingProviderLabel =
    editingDifferentProvider && selectedProvider ? selectedProvider.displayName : null;

  const providerOptions = useMemo(() => {
    if (!state) {
      return [];
    }
    return state.providers.map((p) => ({
      providerId: p.providerId,
      displayName: p.displayName,
      kind: p.providerKind,
      hasExplicitMode: p.hasExplicitMode,
      connectionConfigured: p.connectionConfigured,
      canActivate: p.canActivate,
      blocker: !p.hasExplicitMode
        ? 'No explicit service mode'
        : !p.connectionConfigured
          ? 'Connection not configured'
          : p.activationBlockers[0] ?? null,
    }));
  }, [state]);

  const applyLoadedState = (next: ServiceEditorStateDto): void => {
    setState(next);
    const d = draftRef.current;
    d.switchProvider(next.activeProviderId || next.providers[0]?.providerId || 'none');
    for (const provider of next.providers) {
      const seedDraft = Object.fromEntries(
        Object.entries(provider.fields)
          .filter(([, value]) => value.value !== null && value.value !== undefined)
          .map(([name, value]) => [name, value.value as string])
      );
      d.setDraftForProvider(provider.providerId, seedDraft);
    }
  };

  const load = async (): Promise<void> => {
    setLoading(true);
    setError(null);
    setFieldErrors({});
    try {
      const next = await api.settings.services.get(serviceId);
      applyLoadedState(next);
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load service editor state.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    let cancelled = false;
    (async () => {
      setLoading(true);
      setError(null);
      setFieldErrors({});
      try {
        const next = await api.settings.services.get(serviceId);
        if (cancelled) {
          return;
        }
        applyLoadedState(next);
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load service editor state.');
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [serviceId]);

  const clearFieldError = (fieldName: string): void => {
    setFieldErrors((prev) => {
      const next = { ...prev };
      delete next[fieldName];
      return next;
    });
  };

  const save = async (): Promise<boolean> => {
    if (!state || !selectedProvider) {
      return false;
    }

    if (!selectedProvider.connectionConfigured) {
      setError(
        `Configure the provider connection before saving ${selectedProvider.displayName}: ${selectedProvider.connectionMissingFields.join(', ')}.`
      );
      return false;
    }

    const validation = validateOperativeProviderFields(selectedProvider, draft.activeDraft);
    setFieldErrors(validation);
    if (hasValidationErrors(validation)) {
      setError('Fix the highlighted fields before saving.');
      return false;
    }

    setSaving(true);
    setError(null);
    try {
      const payload = buildSavePayload(selectedProvider, draft.activeDraft);
      if (Object.keys(payload).length > 0 || !selectedProvider.hasExplicitMode) {
        await api.settings.services.updateProviderFields(serviceId, selectedProvider.providerId, payload);
      }
      await api.settings.services.updateActiveProvider(serviceId, selectedProvider.providerId);
      await load();
      return true;
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save provider changes.');
      return false;
    } finally {
      setSaving(false);
    }
  };

  return {
    state,
    loading,
    error,
    saving,
    fieldErrors,
    draft,
    selectedProvider,
    persistedActiveLabel,
    editingProviderLabel,
    providerOptions,
    load,
    save,
    clearFieldError,
  };
}
