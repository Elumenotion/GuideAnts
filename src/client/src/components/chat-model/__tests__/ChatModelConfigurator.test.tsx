import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { ChatModelConfigurator } from '../ChatModelConfigurator';

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
});
