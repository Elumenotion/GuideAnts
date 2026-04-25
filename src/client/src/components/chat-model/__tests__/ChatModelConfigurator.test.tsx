import { describe, it, expect, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { ChatModelConfigurator } from '../ChatModelConfigurator';
import { api } from '../../../services/api';

vi.mock('../../../services/api', () => ({
  api: {
    guides: {
      catalogs: {
        models: vi.fn().mockResolvedValue([
          {
            modelId: 'm-active',
            displayName: 'Active One',
            provider: 'openai-chat',
            isActive: true,
          },
        ]),
      },
    },
  },
}));

describe('ChatModelConfigurator', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.guides.catalogs.models).mockResolvedValue([
      {
        modelId: 'm-active',
        displayName: 'Active One',
        provider: 'openai-chat',
        isActive: true,
      },
    ]);
  });

  it('entity mode lists Use Default Model first', async () => {
    const onChange = vi.fn();
    render(
      <ChatModelConfigurator
        mode="entity"
        modelId=""
        temperature={1}
        topP={1}
        onChange={onChange}
      />
    );

    const select = await screen.findByRole('combobox', { name: /ai model/i });
    const options = within(select).getAllByRole('option');
    expect(options[0]).toHaveTextContent(/use default model/i);
  });

  it('default mode uses catalog placeholder, not Use Default', async () => {
    const onChange = vi.fn();
    render(
      <ChatModelConfigurator
        mode="default"
        modelId=""
        temperature={1}
        topP={1}
        onChange={onChange}
      />
    );

    const select = await screen.findByRole('combobox', { name: /ai model/i });
    const options = within(select).getAllByRole('option');
    expect(options[0]).toHaveTextContent(/select a catalog model/i);
  });

  it('disables config params when disabledReason is set', async () => {
    const onChange = vi.fn();
    render(
      <ChatModelConfigurator
        mode="entity"
        modelId="m-active"
        temperature={0.5}
        topP={0.8}
        onChange={onChange}
        disabledReason="Sampling locked."
      />
    );

    await screen.findByRole('combobox', { name: /ai model/i });
    expect(screen.getByText(/sampling locked/i)).toBeInTheDocument();

    const sliders = screen.getAllByRole('slider');
    for (const s of sliders) {
      expect(s).toBeDisabled();
    }
  });

  it('preserves guide tour id on model select', async () => {
    const onChange = vi.fn();
    const { container } = render(
      <ChatModelConfigurator mode="entity" modelId="" temperature={1} topP={1} onChange={onChange} />
    );

    await screen.findByRole('combobox', { name: /ai model/i });
    const tour = container.querySelector('[data-tour-id="guide.config.model.select"]');
    expect(tour).not.toBeNull();
  });

  it('clears stale reasoning effort when the selected model has no reasoning choices', async () => {
    vi.mocked(api.guides.catalogs.models).mockResolvedValueOnce([
      {
        modelId: 'gemini-2.5-flash',
        displayName: 'Gemini 2.5 Flash',
        provider: 'google-gemini-chat',
        isActive: true,
      },
    ]);

    const onChange = vi.fn();
    render(
      <ChatModelConfigurator
        mode="default"
        modelId="gemini-2.5-flash"
        temperature={0.7}
        topP={0.9}
        reasoningEffort="enabled"
        onChange={onChange}
      />
    );

    await screen.findByRole('combobox', { name: /ai model/i });

    expect(onChange).toHaveBeenCalledWith({
      modelId: 'gemini-2.5-flash',
      temperature: 0.7,
      topP: 0.9,
      reasoningEffort: undefined,
      samplingOverrides: {},
    });
  });

  it('drops stale reasoning effort when switching to a model without reasoning support', async () => {
    vi.mocked(api.guides.catalogs.models).mockResolvedValueOnce([
      {
        modelId: 'gpt-5-mini',
        displayName: 'GPT-5 mini',
        provider: 'openai-chat',
        isActive: true,
        reasoningChoices: ['minimal', 'low', 'medium', 'high'],
      },
      {
        modelId: 'gemini-2.5-flash',
        displayName: 'Gemini 2.5 Flash',
        provider: 'google-gemini-chat',
        isActive: true,
      },
    ]);

    const onChange = vi.fn();
    render(
      <ChatModelConfigurator
        mode="default"
        modelId="gpt-5-mini"
        temperature={0.7}
        topP={0.9}
        reasoningEffort="high"
        onChange={onChange}
      />
    );

    const select = await screen.findByRole('combobox', { name: /ai model/i });
    const option = within(select).getByRole('option', { name: /gemini 2.5 flash/i });

    expect(option).toBeInTheDocument();
    fireEvent.change(select, { target: { value: 'gemini-2.5-flash' } });

    expect(onChange).toHaveBeenCalledWith({
      modelId: 'gemini-2.5-flash',
      temperature: 0.7,
      topP: 0.9,
      reasoningEffort: undefined,
      samplingOverrides: {},
    });
  });
});
