import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ProviderSelector } from '../ProviderSelector';

const options = [
  {
    providerId: 'active',
    displayName: 'Active Provider',
    kind: 'cloud',
    hasExplicitMode: true,
    connectionConfigured: true,
  },
  {
    providerId: 'needs-setup',
    displayName: 'Needs Setup',
    kind: 'local',
    hasExplicitMode: false,
    connectionConfigured: false,
    blocker: 'Missing API key',
  },
];

describe('ProviderSelector', () => {
  it('renders all provider options', () => {
    render(<ProviderSelector value="active" options={options} onChange={vi.fn()} />);

    expect(screen.getByRole('button', { name: /Active Provider/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Needs Setup/i })).toBeInTheDocument();
    expect(screen.getByText('Missing API key')).toBeInTheDocument();
  });

  it('calls onChange when a provider is selected', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();

    render(<ProviderSelector value="active" options={options} onChange={onChange} />);

    await user.click(screen.getByRole('button', { name: /Needs Setup/i }));

    expect(onChange).toHaveBeenCalledWith('needs-setup');
  });

  it('highlights the selected provider', () => {
    render(<ProviderSelector value="active" options={options} onChange={vi.fn()} />);

    expect(screen.getByRole('button', { name: /Active Provider/i })).toHaveClass('border-indigo-500');
    expect(screen.getByRole('button', { name: /Needs Setup/i })).toHaveClass('border-amber-300');
  });
});
