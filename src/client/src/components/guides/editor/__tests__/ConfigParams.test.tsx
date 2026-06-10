import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ConfigParams } from '../ConfigParams';
import { ModelDto } from '../../../../types/guides';

const baseModel: ModelDto = {
  modelId: 'gpt-test',
  displayName: 'GPT Test',
  samplingParameterPolicy: [
    {
      key: 'temperature',
      label: 'Temperature',
      description: 'Controls randomness',
      min: 0,
      max: 2,
      step: 0.1,
      recommendedDefault: 1,
      displayOrder: 1,
    },
    {
      key: 'top_p',
      label: 'Top P',
      description: 'Nucleus sampling',
      min: 0,
      max: 1,
      step: 0.1,
      recommendedDefault: 1,
      displayOrder: 2,
    },
    {
      key: 'presence_penalty',
      label: 'Presence Penalty',
      description: 'Penalty for new topics',
      min: 0,
      max: 2,
      step: 1,
      recommendedDefault: 0,
      displayOrder: 3,
    },
  ],
  reasoningChoices: ['low', 'medium', 'high'],
  defaultReasoningChoice: 'medium',
};

describe('ConfigParams', () => {
  it('returns null when model has no configurable parameters', () => {
    const { container } = render(
      <ConfigParams
        model={{ modelId: 'plain', displayName: 'Plain' }}
        onTemperatureChange={vi.fn()}
        onTopPChange={vi.fn()}
        onReasoningEffortChange={vi.fn()}
      />
    );

    expect(container).toBeEmptyDOMElement();
  });

  it('renders sampling sliders and updates temperature and top_p', () => {
    const onTemperatureChange = vi.fn();
    const onTopPChange = vi.fn();

    render(
      <ConfigParams
        model={baseModel}
        temperature={0.5}
        topP={0.8}
        onTemperatureChange={onTemperatureChange}
        onTopPChange={onTopPChange}
        onReasoningEffortChange={vi.fn()}
      />
    );

    expect(screen.getByText('Configuration Parameters')).toBeInTheDocument();
    expect(screen.getByLabelText('Temperature')).toHaveValue('0.5');
    expect(screen.getByLabelText('Top P')).toHaveValue('0.8');

    fireEvent.change(screen.getByLabelText('Temperature'), { target: { value: '1.2' } });
    fireEvent.change(screen.getByLabelText('Top P'), { target: { value: '0.9' } });

    expect(onTemperatureChange).toHaveBeenCalledWith(1.2);
    expect(onTopPChange).toHaveBeenCalledWith(0.9);
  });

  it('routes custom sampling keys through onSamplingParameterChange', () => {
    const onSamplingParameterChange = vi.fn();

    render(
      <ConfigParams
        model={baseModel}
        samplingOverrides={{ presence_penalty: 1 }}
        onTemperatureChange={vi.fn()}
        onTopPChange={vi.fn()}
        onReasoningEffortChange={vi.fn()}
        onSamplingParameterChange={onSamplingParameterChange}
      />
    );

    fireEvent.change(screen.getByLabelText('Presence Penalty'), { target: { value: '2' } });
    expect(onSamplingParameterChange).toHaveBeenCalledWith('presence_penalty', 2);
  });

  it('renders reasoning effort select and capitalizes choices', () => {
    const onReasoningEffortChange = vi.fn();

    render(
      <ConfigParams
        model={baseModel}
        reasoningEffort="high"
        onTemperatureChange={vi.fn()}
        onTopPChange={vi.fn()}
        onReasoningEffortChange={onReasoningEffortChange}
      />
    );

    const select = screen.getByLabelText('Reasoning Effort') as HTMLSelectElement;
    expect(select.value).toBe('high');
    expect(screen.getByRole('option', { name: 'High' })).toBeInTheDocument();

    fireEvent.change(select, { target: { value: 'low' } });
    expect(onReasoningEffortChange).toHaveBeenCalledWith('low');
  });

  it('locks reasoning select when only one choice is available', () => {
    render(
      <ConfigParams
        model={{
          modelId: 'solo',
          displayName: 'Solo',
          reasoningChoices: ['medium'],
          defaultReasoningChoice: 'medium',
        }}
        onTemperatureChange={vi.fn()}
        onTopPChange={vi.fn()}
        onReasoningEffortChange={vi.fn()}
      />
    );

    expect(screen.getByLabelText('Reasoning Effort')).toBeDisabled();
  });

  it('falls back to default reasoning choice when current value is invalid', () => {
    render(
      <ConfigParams
        model={baseModel}
        reasoningEffort="not-a-choice"
        onTemperatureChange={vi.fn()}
        onTopPChange={vi.fn()}
        onReasoningEffortChange={vi.fn()}
      />
    );

    expect((screen.getByLabelText('Reasoning Effort') as HTMLSelectElement).value).toBe('medium');
  });
});
