import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { OperationalDependencyRow } from '../OperationalDependencyRow';

describe('OperationalDependencyRow', () => {
  it('renders configured dependency with current value', () => {
    render(
      <OperationalDependencyRow
        keyName="HF_TOKEN"
        hasValue
        currentValue="hf_***"
      />
    );

    expect(screen.getByText('HF_TOKEN')).toBeInTheDocument();
    expect(screen.getByText('Configured')).toBeInTheDocument();
    expect(screen.getByText('hf_***')).toBeInTheDocument();
  });

  it('renders missing dependency with custom labels', () => {
    render(
      <OperationalDependencyRow
        keyName="CUSTOM_KEY"
        displayName="Custom Dependency"
        hasValue={false}
        changeHint="Set this in environment variables."
      />
    );

    expect(screen.getByText('Custom Dependency')).toBeInTheDocument();
    expect(screen.getByText('Missing')).toBeInTheDocument();
    expect(screen.getByText('Set this in environment variables.')).toBeInTheDocument();
  });

  it('resolves display name and hint from constants when omitted', () => {
    render(
      <OperationalDependencyRow
        keyName="LlamaCpp:BaseUrl"
        hasValue={false}
      />
    );

    expect(screen.getByText('Llama.cpp Server Base URL')).toBeInTheDocument();
    expect(screen.getByText(/Runtime-owned value/i)).toBeInTheDocument();
  });
});
