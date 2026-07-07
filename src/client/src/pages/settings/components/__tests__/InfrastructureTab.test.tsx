import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ToastProvider } from '../../../../components/common/Toast';
import { InfrastructureTab } from '../InfrastructureTab';
import { api } from '../../../../services/api';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      infrastructure: {
        listDependencies: vi.fn(),
        probe: vi.fn(),
        updateDependency: vi.fn(),
      },
    },
  },
}));

const llamaBaseUrl = {
  key: 'LlamaCpp:BaseUrl',
  currentValue: 'http://localhost/llama-cpp',
  readOnly: false,
  usedByProviderIds: [],
  source: 'runtime',
  hasValue: true,
  isSecret: false,
  kind: 'url',
};

describe('InfrastructureTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.infrastructure.listDependencies).mockResolvedValue([llamaBaseUrl] as never);
    vi.mocked(api.settings.infrastructure.probe).mockResolvedValue({
      generatedUtc: '2026-01-01T00:00:00Z',
      results: [
        {
          id: 'LlamaCpp:BaseUrl',
          kind: 'url',
          target: 'http://localhost/llama-cpp',
          reachable: true,
        },
      ],
    } as never);
    vi.mocked(api.settings.infrastructure.updateDependency).mockResolvedValue({
      ...llamaBaseUrl,
      currentValue: 'http://localhost:8080/llama-cpp',
    } as never);
  });

  it('loads dependencies, probes them, and saves edits', async () => {
    const user = userEvent.setup();

    render(
      <ToastProvider>
        <InfrastructureTab focusedRuntimeKey="LlamaCpp:BaseUrl" />
      </ToastProvider>,
    );

    expect(await screen.findByText('Llama.cpp Server Base URL')).toBeInTheDocument();
    await waitFor(() => expect(api.settings.infrastructure.probe).toHaveBeenCalled());

    const input = await screen.findByLabelText(/Llama\.cpp Server Base URL value/i);
    await user.clear(input);
    await user.type(input, 'http://localhost:8080/llama-cpp');
    await user.click(screen.getByRole('button', { name: /Save/i }));

    await waitFor(() => {
      expect(api.settings.infrastructure.updateDependency).toHaveBeenCalledWith(
        'LlamaCpp:BaseUrl',
        'http://localhost:8080/llama-cpp',
      );
    });
  });

  it('shows validation errors for invalid llama base URLs', async () => {
    const user = userEvent.setup();
    render(
      <ToastProvider>
        <InfrastructureTab />
      </ToastProvider>,
    );

    const input = await screen.findByLabelText(/Llama\.cpp Server Base URL value/i);
    await user.clear(input);
    await user.type(input, 'http://localhost/wrong-path');
    await user.click(screen.getByRole('button', { name: /Save/i }));

    expect(await screen.findByText(/Must include the '\/llama-cpp' path/i)).toBeInTheDocument();
    expect(api.settings.infrastructure.updateDependency).not.toHaveBeenCalled();
  });
});
