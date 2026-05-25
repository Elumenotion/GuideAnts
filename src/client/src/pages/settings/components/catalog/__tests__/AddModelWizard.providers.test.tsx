import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AddModelWizard } from '../AddModelWizard';

describe('AddModelWizard provider options', () => {
  it('shows hugging face and hides incomplete providers in provider selector', () => {
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

    expect(screen.getByRole('option', { name: 'Google Gemini API' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Hugging Face Inference' })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'OpenRouter' })).not.toBeInTheDocument();
  });
});
