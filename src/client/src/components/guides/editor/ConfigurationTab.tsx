import React from 'react';
import {
  ChatModelConfigurator,
  ChatModelConfigValue,
} from '../../chat-model/ChatModelConfigurator';
import { ModelDto } from '../../../types/guides';

interface ConfigurationTabProps {
  modelId?: string;
  temperature: number;
  topP: number;
  reasoningEffort?: string;
  samplingOverrides?: Record<string, number>;
  onModelIdChange: (modelId?: string, model?: ModelDto) => void;
  onTemperatureChange: (temperature: number) => void;
  onTopPChange: (topP: number) => void;
  onReasoningEffortChange: (reasoningEffort: string) => void;
  onSamplingOverridesChange: (overrides: Record<string, number>) => void;
  /** When set, sampling controls are read-only (e.g. global override on). */
  paramsDisabledReason?: string;
}

const ConfigurationTabComponent = ({
  modelId,
  temperature,
  topP,
  reasoningEffort,
  samplingOverrides,
  onModelIdChange,
  onTemperatureChange,
  onTopPChange,
  onReasoningEffortChange,
  onSamplingOverridesChange,
  paramsDisabledReason,
}: ConfigurationTabProps) => {
  const handleConfiguratorChange = (next: ChatModelConfigValue) => {
    onModelIdChange(next.modelId, undefined);
    onTemperatureChange(next.temperature);
    onTopPChange(next.topP);
    onReasoningEffortChange(next.reasoningEffort ?? '');
    onSamplingOverridesChange(next.samplingOverrides ?? {});
  };

  return (
    <ChatModelConfigurator
      mode="entity"
      modelId={modelId ?? ''}
      temperature={temperature}
      topP={topP}
      reasoningEffort={reasoningEffort}
      samplingOverrides={samplingOverrides}
      onChange={handleConfiguratorChange}
      disabledReason={paramsDisabledReason}
    />
  );
};

export const ConfigurationTab = React.memo(ConfigurationTabComponent, (prevProps, nextProps) => {
  return (
    prevProps.modelId === nextProps.modelId &&
    prevProps.temperature === nextProps.temperature &&
    prevProps.topP === nextProps.topP &&
    prevProps.reasoningEffort === nextProps.reasoningEffort &&
    prevProps.samplingOverrides === nextProps.samplingOverrides &&
    prevProps.paramsDisabledReason === nextProps.paramsDisabledReason
  );
});
