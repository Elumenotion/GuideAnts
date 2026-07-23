import { useCallback, useEffect } from 'react';
import { FaSpinner } from 'react-icons/fa';
import { TextActionButton } from '../../../pages/settings/components/shared/ActionButtons';
import type { SettingsModelDto } from '../../../types/settings';
import { useCuratedOperationPolling } from '../useOperationPolling';
import { LlamaCuratedCompletion } from './LlamaCuratedCompletion';
import { LlamaCuratedModelPicker } from './LlamaCuratedModelPicker';
import { LlamaCuratedProgress } from './LlamaCuratedProgress';
import { LlamaCuratedQuantPicker } from './LlamaCuratedQuantPicker';
import { LlamaCuratedReview } from './LlamaCuratedReview';
import { getSelectedDefinition, getSelectedQuant, type CuratedOnboardingActions } from './types';
import { useCuratedOnboarding } from './useCuratedOnboarding';

export interface LlamaCuratedOnboardingFlowProps {
  onboardingUi: 'settings' | 'wizard';
  enabled?: boolean;
  onOperationStarted?: (operationId: string, meta: {
    catalogModelId: string;
    catalogDisplayName: string;
    routerModelId: string;
  }) => void;
  onCompleted?: (result: { catalogModel: SettingsModelDto | null; routerModelId: string }) => void;
  onSetDefault?: (catalogModelId: string) => Promise<void>;
  onViewInstalled?: (catalogModelId: string) => void;
  onClose?: () => void;
  onStepChange?: (step: string) => void;
  renderFooter?: (actions: {
    canBack: boolean;
    canContinue: boolean;
    canSubmit: boolean;
    submitting: boolean;
    onBack: () => void;
    onContinue: () => void;
    onSubmit: () => void;
  }) => React.ReactNode;
}

export function LlamaCuratedOnboardingFlow({
  onboardingUi,
  enabled = true,
  onOperationStarted,
  onCompleted,
  onSetDefault,
  onViewInstalled,
  onClose,
  onStepChange,
  renderFooter,
}: LlamaCuratedOnboardingFlowProps) {
  const [state, actions] = useCuratedOnboarding({ onboardingUi, enabled, onOperationStarted });
  const selectedDefinition = getSelectedDefinition(state);
  const selectedQuant = getSelectedQuant(state);

  useEffect(() => {
    onStepChange?.(state.step);
  }, [onStepChange, state.step]);

  const handleOperationUpdate = useCallback((operation: Parameters<CuratedOnboardingActions['applyOperationUpdate']>[0]) => {
    actions.applyOperationUpdate(operation);
    if (operation.status === 'completed') {
      onCompleted?.({
        catalogModel: operation.catalogModel ?? null,
        routerModelId: operation.routerModelId,
      });
    }
  }, [actions, onCompleted]);

  useCuratedOperationPolling({
    operationId: state.operationId,
    enabled: enabled && !!state.operationId && state.step === 'progress',
    onUpdate: handleOperationUpdate,
    onTerminal: handleOperationUpdate,
  });

  const handleContinue = () => {
    if (state.step === 'model' && state.selectedDefinitionId) {
      actions.goToStep('quant');
      return;
    }
    if (state.step === 'quant' && state.selectedQuantId && !state.selectionInvalidated) {
      actions.goToStep('review');
    }
  };

  const footer = renderFooter?.({
    canBack: state.step === 'quant' || state.step === 'review',
    canContinue: actions.canContinue(),
    canSubmit: state.step === 'review' && actions.canContinue(),
    submitting: state.submitting,
    onBack: actions.goBack,
    onContinue: handleContinue,
    onSubmit: () => void actions.submit(),
  });

  return (
    <div className="space-y-4">
      {state.step === 'model' ? (
        <LlamaCuratedModelPicker
          models={state.catalogResponse?.models ?? []}
          searchQuery={state.searchQuery}
          selectedDefinitionId={state.selectedDefinitionId}
          loading={state.catalogLoading}
          error={state.catalogError}
          onSearchChange={actions.setSearchQuery}
          onSelect={actions.selectModel}
          onRetry={() => void actions.loadCatalog()}
        />
      ) : null}

      {state.step === 'quant' && selectedDefinition ? (
        <LlamaCuratedQuantPicker
          definition={selectedDefinition}
          quantsResponse={state.quantsResponse}
          selectedQuantId={state.selectedQuantId}
          loading={state.quantsLoading}
          error={state.quantsError}
          selectionInvalidated={state.selectionInvalidated}
          onSelect={actions.selectQuant}
          onRefresh={() => void actions.refreshQuants()}
        />
      ) : null}

      {state.step === 'review' && selectedDefinition && selectedQuant && state.quantsResponse ? (
        <LlamaCuratedReview
          definition={selectedDefinition}
          quantsResponse={state.quantsResponse}
          selectedQuant={selectedQuant}
        />
      ) : null}

      {/* Wizard hosts install progress in LocalAiModelsStep's Installing list; avoid a second copy here. */}
      {state.step === 'progress' && onboardingUi !== 'wizard' ? (
        <LlamaCuratedProgress
          status={state.operation?.status ?? 'queued'}
          progress={state.operation?.progress ?? null}
          logLine={state.operation?.logLine}
          error={state.submitError ?? state.operation?.error ?? null}
        />
      ) : null}

      {state.submitError && state.step !== 'progress' ? (
        <div
          role="alert"
          className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700"
        >
          <div className="font-medium">{state.submitError.code}</div>
          <div>{state.submitError.message}</div>
          {state.submitError.remediation ? (
            <div className="mt-1 text-xs">{state.submitError.remediation}</div>
          ) : null}
        </div>
      ) : null}

      {state.step === 'completed' ? (
        <LlamaCuratedCompletion
          catalogModel={state.completedCatalogModel}
          routerModelId={state.completedRouterModelId ?? state.operation?.routerModelId ?? ''}
          onSetDefault={onSetDefault}
          onViewInstalled={onViewInstalled}
          onClose={onClose}
        />
      ) : null}

      {footer ?? (
        <div className="flex flex-wrap gap-2">
          {(state.step === 'quant' || state.step === 'review') ? (
            <TextActionButton tone="neutral" onClick={actions.goBack} title="Back">
              Back
            </TextActionButton>
          ) : null}
          {state.step === 'model' || state.step === 'quant' ? (
            <TextActionButton
              tone="primary"
              disabled={!actions.canContinue()}
              onClick={handleContinue}
              title="Continue"
            >
              Continue
            </TextActionButton>
          ) : null}
          {state.step === 'review' ? (
            <TextActionButton
              tone="primary"
              disabled={!actions.canContinue() || state.submitting}
              icon={state.submitting ? <FaSpinner className="animate-spin" /> : undefined}
              onClick={() => void actions.submit()}
              title="Install model"
            >
              Install model
            </TextActionButton>
          ) : null}
          {state.step === 'progress' && (state.operation?.status === 'error' || state.submitError) ? (
            <TextActionButton
              tone="primary"
              disabled={state.submitting}
              icon={state.submitting ? <FaSpinner className="animate-spin" /> : undefined}
              onClick={() => void actions.retrySubmit()}
              title="Retry install"
            >
              Retry
            </TextActionButton>
          ) : null}
        </div>
      )}
    </div>
  );
}
