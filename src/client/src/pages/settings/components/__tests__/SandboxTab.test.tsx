import '@testing-library/jest-dom';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { ToastProvider } from '../../../../components/common/Toast';
import { api } from '../../../../services/api';
import { SettingsSectionDto } from '../../../../types/settings';
import { SandboxTab } from '../SandboxTab';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      getSection: vi.fn(),
      updateSection: vi.fn(),
    },
  },
}));

const baseSection: SettingsSectionDto = {
  sectionName: 'ScriptExecution',
  displayName: 'ScriptExecution',
  schemaVersion: 1,
  rowVersion: 'row-1',
  updatedUtc: '2026-04-28T00:00:00Z',
  payload: {
    TimeoutSeconds: 600,
  },
  secretHasValue: {},
};

function renderSandboxTab() {
  return render(
    <ToastProvider>
      <SandboxTab />
    </ToastProvider>
  );
}

describe('SandboxTab', () => {
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

  it('renders sandbox settings and saves timeout changes', async () => {
    const user = userEvent.setup();
    renderSandboxTab();

    expect(await screen.findByRole('heading', { name: 'Sandbox' })).toBeInTheDocument();
    expect(screen.getByLabelText('Script execution timeout (seconds)')).toHaveValue(600);

    await user.clear(screen.getByLabelText('Script execution timeout (seconds)'));
    await user.type(screen.getByLabelText('Script execution timeout (seconds)'), '900');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(api.settings.updateSection).toHaveBeenCalledWith(
        'ScriptExecution',
        expect.objectContaining({
          rowVersion: 'row-1',
          payload: {
            TimeoutSeconds: 900,
          },
        })
      );
    });
    expect(await screen.findByText('New script execution timeouts apply to the next sandbox tool run.')).toBeInTheDocument();
  });
});
