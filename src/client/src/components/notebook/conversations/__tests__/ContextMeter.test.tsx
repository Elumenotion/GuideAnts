import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { describe, it, expect } from 'vitest';
import ContextMeter from '../ContextMeter';
import type { ConversationContextStatus } from '../../../../types/notebook';

const knownStatus: ConversationContextStatus = {
  contextWindowTokens: 8192,
  estimatedPromptTokens: 4096,
  boundaryTurnIndex: null,
  estimateSource: 'ProviderUsage',
  modelDeploymentId: 'gpt-4o-mini',
  contextWindowSource: 'Catalog',
};

describe('ContextMeter', () => {
  it('renders an explicit unknown state when contextStatus is null', () => {
    render(<ContextMeter contextStatus={null} />);

    expect(screen.getByText(/context.*unknown/i)).toBeInTheDocument();
  });

  it('renders an explicit unknown state when contextWindowTokens is null', () => {
    render(<ContextMeter contextStatus={{ ...knownStatus, contextWindowTokens: null }} />);

    expect(screen.getByText(/context.*unknown/i)).toBeInTheDocument();
  });

  it('renders known utilization as a percentage of the window', () => {
    render(<ContextMeter contextStatus={knownStatus} />);

    expect(screen.getByText('50%')).toBeInTheDocument();
    expect(screen.getByText(/4,096/)).toBeInTheDocument();
    expect(screen.getByText(/8,192/)).toBeInTheDocument();
  });

  it('shows a compacted indicator when boundaryTurnIndex is set', () => {
    render(<ContextMeter contextStatus={{ ...knownStatus, boundaryTurnIndex: 3 }} />);

    expect(screen.getByText(/compacted/i)).toBeInTheDocument();
  });

  it('does not show a compacted indicator when never compacted', () => {
    render(<ContextMeter contextStatus={knownStatus} />);

    expect(screen.queryByText(/compacted/i)).not.toBeInTheDocument();
  });

  it('uses text-gray-500 at a normal usage percentage', () => {
    render(<ContextMeter contextStatus={knownStatus} />);

    expect(screen.getByTestId('context-meter')).toHaveClass('text-gray-500');
  });

  it('uses text-gray-500 (not gray-400) for the unknown state', () => {
    render(<ContextMeter contextStatus={null} />);

    const meter = screen.getByTestId('context-meter');
    expect(meter).toHaveClass('text-gray-500');
    expect(meter).not.toHaveClass('text-gray-400');
  });

  it('uses text-amber-600 at >=75% utilization', () => {
    render(<ContextMeter contextStatus={{ ...knownStatus, estimatedPromptTokens: 6200, contextWindowTokens: 8192 }} />);

    expect(screen.getByTestId('context-meter')).toHaveClass('text-amber-600');
  });

  it('uses text-red-600 at >=90% utilization', () => {
    render(<ContextMeter contextStatus={{ ...knownStatus, estimatedPromptTokens: 7500, contextWindowTokens: 8192 }} />);

    expect(screen.getByTestId('context-meter')).toHaveClass('text-red-600');
  });
});
