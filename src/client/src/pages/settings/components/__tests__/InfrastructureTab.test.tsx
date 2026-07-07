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

  it('surfaces load failures, unreachable probes, and prefix warnings', async () => {
    vi.mocked(api.settings.infrastructure.listDependencies).mockResolvedValue([
      {
        ...llamaBaseUrl,
        currentValue: 'localhost/llama-cpp',
      },
    ] as never);
    vi.mocked(api.settings.infrastructure.probe).mockResolvedValue({
      generatedUtc: '2026-01-01T00:00:00Z',
      results: [
        {
          id: 'LlamaCpp:BaseUrl',
          kind: 'url',
          target: 'localhost/llama-cpp',
          reachable: false,
          error: 'Connection refused',
        },
      ],
    } as never);

    render(
      <ToastProvider>
        <InfrastructureTab />
      </ToastProvider>,
    );

    expect(await screen.findByText(/Must start with/i)).toBeInTheDocument();
    expect(await screen.findByText('Unreachable')).toBeInTheDocument();
    expect(screen.getByText('Connection refused')).toBeInTheDocument();
  });

  it('resets draft edits and reports path probe health states', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.infrastructure.listDependencies).mockResolvedValue([
      llamaBaseUrl,
      {
        key: 'ContentRoot',
        currentValue: '/data/content',
        readOnly: false,
        usedByProviderIds: [],
        source: 'runtime',
        hasValue: true,
        isSecret: false,
        kind: 'path',
      },
    ] as never);
    vi.mocked(api.settings.infrastructure.probe).mockResolvedValue({
      generatedUtc: '2026-01-01T00:00:00Z',
      results: [
        {
          id: 'LlamaCpp:BaseUrl',
          kind: 'url',
          target: 'http://localhost/llama-cpp',
          reachable: true,
          statusCode: 200,
        },
        {
          id: 'ContentRoot',
          kind: 'path',
          target: '/data/content',
          exists: true,
          writable: false,
          error: 'Read-only mount',
        },
      ],
    } as never);

    render(
      <ToastProvider>
        <InfrastructureTab />
      </ToastProvider>,
    );

    const input = await screen.findByLabelText(/Llama\.cpp Server Base URL value/i);
    await user.clear(input);
    await user.type(input, 'http://localhost:9000/llama-cpp');
    await user.click(screen.getByTitle('Reset Llama.cpp Server Base URL draft'));
    expect(await screen.findByDisplayValue('http://localhost/llama-cpp')).toBeInTheDocument();
    expect(await screen.findByText('Exists · not writable')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Probe all/i }));
    await waitFor(() => expect(api.settings.infrastructure.probe).toHaveBeenCalledTimes(2));
  });

  it('calls the focus handler after deep-linking to a runtime key', async () => {
    const onFocusedRuntimeKeyHandled = vi.fn();
    HTMLElement.prototype.scrollIntoView = vi.fn();

    render(
      <ToastProvider>
        <InfrastructureTab
          focusedRuntimeKey="LlamaCpp:BaseUrl"
          onFocusedRuntimeKeyHandled={onFocusedRuntimeKeyHandled}
        />
      </ToastProvider>,
    );

    await screen.findByText('Llama.cpp Server Base URL');
    await waitFor(() => expect(onFocusedRuntimeKeyHandled).toHaveBeenCalled(), { timeout: 3000 });
  });
});
