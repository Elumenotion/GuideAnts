import { useCallback, useEffect, useMemo, useReducer } from 'react';
import { api } from '../../../services/api';
import type { AddModelErrorDto, LlamaOperationStatusDto } from '../../../types/settings';
import { buildCuratedAddModelRequest } from './buildCuratedRequest';
import { parseAddModelErrorFromUnknown } from '../mapOperationStatus';
import {
  type CuratedOnboardingActions,
  type CuratedOnboardingState,
  getSelectedDefinition,
  getSelectedQuant,
  type LocalModelOnboardingMode,
  type CuratedOnboardingStep,
} from './types';

type Action =
  | { type: 'setMode'; mode: LocalModelOnboardingMode }
  | { type: 'setSearchQuery'; query: string }
  | { type: 'catalogLoading' }
  | { type: 'catalogLoaded'; response: CuratedOnboardingState['catalogResponse'] }
  | { type: 'catalogError'; error: string }
  | { type: 'selectModel'; definitionId: string }
  | { type: 'quantsLoading' }
  | { type: 'quantsLoaded'; response: CuratedOnboardingState['quantsResponse'] }
  | { type: 'quantsError'; error: string }
  | { type: 'selectQuant'; quantId: string }
  | { type: 'invalidateSelection' }
  | { type: 'setStep'; step: CuratedOnboardingStep }
  | { type: 'submitting' }
  | { type: 'submitError'; error: AddModelErrorDto | null }
  | { type: 'operationStarted'; operationId: string }
  | { type: 'operationUpdated'; operation: LlamaOperationStatusDto }
  | { type: 'resetOperation' };

function createInitialState(): CuratedOnboardingState {
  return {
    mode: 'curated',
    step: 'model',
    catalogLoading: false,
    catalogError: null,
    catalogResponse: null,
    searchQuery: '',
    selectedDefinitionId: null,
    quantsLoading: false,
    quantsError: null,
    quantsResponse: null,
    selectedQuantId: null,
    selectionInvalidated: false,
    submitting: false,
    submitError: null,
    operationId: null,
    operation: null,
    completedCatalogModel: null,
    completedRouterModelId: null,
  };
}

function reducer(state: CuratedOnboardingState, action: Action): CuratedOnboardingState {
  switch (action.type) {
    case 'setMode':
      return { ...createInitialState(), mode: action.mode };
    case 'setSearchQuery':
      return { ...state, searchQuery: action.query };
    case 'catalogLoading':
      return { ...state, catalogLoading: true, catalogError: null };
    case 'catalogLoaded':
      return { ...state, catalogLoading: false, catalogResponse: action.response, catalogError: null };
    case 'catalogError':
      return { ...state, catalogLoading: false, catalogError: action.error };
    case 'selectModel':
      return {
        ...state,
        selectedDefinitionId: action.definitionId,
        step: 'quant',
        quantsResponse: null,
        quantsError: null,
        selectedQuantId: null,
        selectionInvalidated: false,
        submitError: null,
      };
    case 'quantsLoading':
      return { ...state, quantsLoading: true, quantsError: null };
    case 'quantsLoaded':
      return {
        ...state,
        quantsLoading: false,
        quantsResponse: action.response,
        quantsError: null,
        selectionInvalidated: false,
      };
    case 'quantsError':
      return { ...state, quantsLoading: false, quantsError: action.error };
    case 'selectQuant':
      return { ...state, selectedQuantId: action.quantId, selectionInvalidated: false, submitError: null };
    case 'invalidateSelection':
      return {
        ...state,
        selectedQuantId: null,
        selectionInvalidated: true,
        step: state.step === 'review' ? 'quant' : state.step,
      };
    case 'setStep':
      return { ...state, step: action.step };
    case 'submitting':
      return { ...state, submitting: true, submitError: null };
    case 'submitError':
      return { ...state, submitting: false, submitError: action.error };
    case 'operationStarted':
      return {
        ...state,
        submitting: false,
        operationId: action.operationId,
        step: 'progress',
        submitError: null,
      };
    case 'operationUpdated': {
      const terminal = action.operation.status === 'completed' || action.operation.status === 'error';
      return {
        ...state,
        operation: action.operation,
        completedCatalogModel: action.operation.catalogModel ?? state.completedCatalogModel,
        completedRouterModelId: action.operation.routerModelId || state.completedRouterModelId,
        step: action.operation.status === 'completed'
          ? 'completed'
          : action.operation.status === 'error'
            ? 'progress'
            : state.step,
        submitting: false,
        operationId: terminal ? state.operationId : state.operationId,
      };
    }
    case 'resetOperation':
      return {
        ...state,
        operationId: null,
        operation: null,
        submitError: null,
        submitting: false,
      };
    default:
      return state;
  }
}

interface UseCuratedOnboardingOptions {
  onboardingUi: 'settings' | 'wizard';
  enabled?: boolean;
  onOperationStarted?: (operationId: string, meta: {
    catalogModelId: string;
    catalogDisplayName: string;
    routerModelId: string;
  }) => void;
}

export function useCuratedOnboarding({
  onboardingUi,
  enabled = true,
  onOperationStarted,
}: UseCuratedOnboardingOptions): [CuratedOnboardingState, CuratedOnboardingActions] {
  const [state, dispatch] = useReducer(reducer, undefined, createInitialState);

  const selectedDefinition = useMemo(() => getSelectedDefinition(state), [state]);
  const selectedQuant = useMemo(() => getSelectedQuant(state), [state]);

  const loadCatalog = useCallback(async () => {
    dispatch({ type: 'catalogLoading' });
    try {
      const response = await api.settings.getLlamaCatalog();
      dispatch({ type: 'catalogLoaded', response });
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to load curated catalog.';
      dispatch({ type: 'catalogError', error: message });
    }
  }, []);

  const refreshQuants = useCallback(async () => {
    if (!selectedDefinition || !state.catalogResponse) {
      return;
    }
    const previousRevision = state.quantsResponse?.resolvedRevision ?? null;
    dispatch({ type: 'quantsLoading' });
    try {
      const response = await api.settings.getLlamaCatalogQuants(
        selectedDefinition.id,
        state.catalogResponse.catalogVersion,
      );
      dispatch({ type: 'quantsLoaded', response });
      if (previousRevision && previousRevision !== response.resolvedRevision) {
        dispatch({ type: 'invalidateSelection' });
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to load quant groups.';
      dispatch({ type: 'quantsError', error: message });
    }
  }, [selectedDefinition, state.catalogResponse, state.quantsResponse?.resolvedRevision]);

  useEffect(() => {
    if (!enabled || state.mode !== 'curated') {
      return;
    }
    if (!state.catalogResponse && !state.catalogLoading && !state.catalogError) {
      void loadCatalog();
    }
  }, [enabled, loadCatalog, state.catalogError, state.catalogLoading, state.catalogResponse, state.mode]);

  useEffect(() => {
    if (!enabled || state.mode !== 'curated' || !selectedDefinition || state.step === 'model') {
      return;
    }
    if (!state.quantsResponse && !state.quantsLoading) {
      void refreshQuants();
    }
  }, [
    enabled,
    refreshQuants,
    selectedDefinition,
    state.mode,
    state.quantsLoading,
    state.quantsResponse,
    state.step,
  ]);

  const submit = useCallback(async () => {
    if (!selectedDefinition || !selectedQuant || !state.catalogResponse || !state.quantsResponse) {
      return;
    }
    if (state.selectionInvalidated) {
      return;
    }

    dispatch({ type: 'submitting' });
    try {
      const request = buildCuratedAddModelRequest(
        selectedDefinition,
        state.catalogResponse.catalogVersion,
        selectedQuant.id,
        state.quantsResponse.resolvedRevision,
        { onboardingUi },
      );
      const response = await api.settings.addModel(request);
      if (!response.operationId) {
        dispatch({
          type: 'submitError',
          error: {
            code: 'MISSING_OPERATION_ID',
            step: 'submit',
            message: 'Server did not return an operation id for curated install.',
          },
        });
        return;
      }
      dispatch({ type: 'operationStarted', operationId: response.operationId });
      onOperationStarted?.(response.operationId, {
        catalogModelId: selectedDefinition.defaults.catalogModelId,
        catalogDisplayName: selectedDefinition.display.name,
        routerModelId: selectedDefinition.defaults.routerModelId,
      });
    } catch (error) {
      dispatch({ type: 'submitError', error: parseAddModelErrorFromUnknown(error) ?? {
        code: 'SUBMIT_FAILED',
        step: 'submit',
        message: error instanceof Error ? error.message : 'Failed to start curated install.',
      } });
    }
  }, [
    onboardingUi,
    selectedDefinition,
    selectedQuant,
    state.catalogResponse,
    state.quantsResponse,
    state.selectionInvalidated,
    onOperationStarted,
  ]);

  const canContinue = useCallback(() => {
    if (state.step === 'model') {
      return !!state.selectedDefinitionId;
    }
    if (state.step === 'quant') {
      return !!state.selectedQuantId && !state.selectionInvalidated && !state.quantsLoading;
    }
    if (state.step === 'review') {
      return !!state.selectedQuantId && !state.selectionInvalidated;
    }
    return false;
  }, [state.quantsLoading, state.selectedDefinitionId, state.selectedQuantId, state.selectionInvalidated, state.step]);

  const goBack = useCallback(() => {
    if (state.step === 'quant') {
      dispatch({ type: 'setStep', step: 'model' });
      return;
    }
    if (state.step === 'review') {
      dispatch({ type: 'setStep', step: 'quant' });
    }
  }, [state.step]);

  const actions: CuratedOnboardingActions = useMemo(() => ({
    setMode: (mode) => dispatch({ type: 'setMode', mode }),
    setSearchQuery: (query) => dispatch({ type: 'setSearchQuery', query }),
    selectModel: (definitionId) => dispatch({ type: 'selectModel', definitionId }),
    selectQuant: (quantId) => dispatch({ type: 'selectQuant', quantId }),
    refreshQuants: refreshQuants,
    goToStep: (step) => dispatch({ type: 'setStep', step }),
    goBack,
    canContinue,
    submit,
    retrySubmit: submit,
    loadCatalog,
    applyOperationUpdate: (operation) => dispatch({ type: 'operationUpdated', operation }),
  }), [canContinue, goBack, loadCatalog, refreshQuants, submit]);

  return [state, actions];
}