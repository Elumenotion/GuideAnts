import '@testing-library/jest-dom';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { ToastProvider } from '../../../../components/common/Toast';
import { api } from '../../../../services/api';
import { SettingsSectionDto } from '../../../../types/settings';
import { TelemetryTab } from '../TelemetryTab';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      getSection: vi.fn(),
      updateSection: vi.fn(),
    },
  },
}));

const baseSection: SettingsSectionDto = {
  sectionName: 'Telemetry',
  displayName: 'Telemetry',
  schemaVersion: 1,
  rowVersion: 'row-1',
  updatedUtc: '2026-04-28T00:00:00Z',
  payload: {
    GuideAntsApiBackgroundJobs: 'Information',
  },
  secretHasValue: {},
};

function renderTelemetry() {
  return render(
    <ToastProvider>
      <TelemetryTab />
    </ToastProvider>
  );
}

describe('TelemetryTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.settings.getSection as any).mockResolvedValue(baseSection);
    (api.settings.updateSection as any).mockImplementation((_sectionName: string, request: { payload: Record<string, unknown> }) =>
      Promise.resolve({
        ...baseSection,
        rowVersion: 'row-2',
        payload: request.payload,
      })
    );
  });

  it('renders telemetry settings and saves advanced category edits', async () => {
    const user = userEvent.setup();
    renderTelemetry();

    expect(await screen.findByRole('heading', { name: 'Telemetry' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /advanced categories/i }));
    await user.selectOptions(screen.getByLabelText('Background jobs log level'), 'Debug');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(api.settings.updateSection).toHaveBeenCalledWith(
        'Telemetry',
        expect.objectContaining({
          rowVersion: 'row-1',
          payload: expect.objectContaining({
            GuideAntsApiBackgroundJobs: 'Debug',
          }),
        })
      );
    });
    expect(await screen.findByText('API logging uses the new settings immediately.')).toBeInTheDocument();
  });

  it('applies subsystem presets to the underlying category payload', async () => {
    const user = userEvent.setup();
    renderTelemetry();

    await screen.findByRole('heading', { name: 'Telemetry' });
    await user.click(screen.getByRole('button', { name: 'Background jobs Investigating' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(api.settings.updateSection).toHaveBeenCalledWith(
        'Telemetry',
        expect.objectContaining({
          payload: expect.objectContaining({
            GuideAntsApiBackgroundJobs: 'Debug',
          }),
        })
      );
    });
  });

  it('shows save conflicts as actionable errors', async () => {
    const user = userEvent.setup();
    (api.settings.updateSection as any).mockRejectedValueOnce({ status: 409 });
    renderTelemetry();

    await screen.findByRole('heading', { name: 'Telemetry' });
    await user.click(screen.getByRole('button', { name: 'Background jobs Investigating' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Refresh the Telemetry tab and try again.')).toBeInTheDocument();
  });
});
