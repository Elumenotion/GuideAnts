import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AddModelWizard } from '../AddModelWizard';

describe('AddModelWizard provider options', () => {
  it('shows new cloud provider options in provider selector', () => {
    render(
      <AddModelWizard
        isOpen
        providerPreselect={null}
        profiles={[]}
        profilesLoading={false}
        inventory={[]}
        onClose={vi.fn()}
        onCreateRuntimeProfileTemplate={vi.fn(async () => {})}
        onCreateCustomRuntimeProfile={vi.fn(async () => {
          throw new Error('not used');
        })}
        onCatalogChanged={vi.fn(async () => {})}
        onSetActiveAddOperation={vi.fn()}
      />
    );

    expect(screen.getByRole('option', { name: 'google-gemini-chat' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'hf-inference-chat' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'openrouter-chat' })).toBeInTheDocument();
  });
});
