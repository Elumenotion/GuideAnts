import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ServiceEditorShell } from '../ServiceEditorShell';

describe('ServiceEditorShell', () => {
  const baseProps = {
    serviceName: 'Speech Transcription',
    activeProviderLabel: 'Azure Speech',
    readinessStatus: 'ready' as const,
    readinessSummary: 'Service is ready.',
    providerSelector: <div data-testid="selector">Selector</div>,
    providerSettings: <div data-testid="provider-settings">Provider settings</div>,
    actions: <button type="button">Save</button>,
  };

  it('renders service metadata and child sections', () => {
    render(<ServiceEditorShell {...baseProps} />);

    expect(screen.getByRole('heading', { name: 'Speech Transcription' })).toBeInTheDocument();
    expect(screen.getByText(/Active provider: Azure Speech/)).toBeInTheDocument();
    expect(screen.getByTestId('selector')).toBeInTheDocument();
    expect(screen.getByTestId('provider-settings')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
    expect(screen.getByText('ready')).toBeInTheDocument();
  });

  it('shows editing provider notice when editingProviderLabel is set', () => {
    render(
      <ServiceEditorShell
        {...baseProps}
        editingProviderLabel="Local ASR"
      />
    );

    expect(screen.getByText(/Editing configuration for:/)).toBeInTheDocument();
    expect(screen.getByText('Local ASR')).toBeInTheDocument();
  });

  it('overrides readiness when active provider is not configured', () => {
    render(
      <ServiceEditorShell
        {...baseProps}
        activeProviderLabel="Not configured"
        readinessStatus="ready"
        readinessSummary="Ignored summary"
      />
    );

    expect(screen.getByText('Not configured')).toBeInTheDocument();
    expect(screen.getByText(/No active provider configured yet/)).toBeInTheDocument();
  });

  it('renders optional service settings section', () => {
    render(
      <ServiceEditorShell
        {...baseProps}
        serviceSettings={<div data-testid="service-settings">Service settings</div>}
      />
    );

    expect(screen.getByRole('heading', { name: 'Service Settings' })).toBeInTheDocument();
    expect(screen.getByTestId('service-settings')).toBeInTheDocument();
  });
});
