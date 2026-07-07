import { describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { RuntimeProfileDialog } from '../RuntimeProfileDialog';
import { createEmptyProfileForm } from '../../utils';

describe('RuntimeProfileDialog', () => {
  it('toggles providers and submits create profile', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    const onChange = vi.fn();

    render(
      <RuntimeProfileDialog
        isOpen
        editingProfileId={null}
        value={createEmptyProfileForm()}
        submitting={false}
        onChange={onChange}
        onInsertTemplate={vi.fn()}
        onClose={vi.fn()}
        onSubmit={onSubmit}
      />,
    );

    await user.click(screen.getByRole('checkbox', { name: 'openai-chat' }));
    expect(onChange).toHaveBeenCalledWith('providers', ['openai-chat']);

    await user.click(screen.getByRole('button', { name: 'Create profile' }));
    expect(onSubmit).toHaveBeenCalled();
  });

  it('shows edit mode title when updating an existing profile', () => {
    render(
      <RuntimeProfileDialog
        isOpen
        editingProfileId="qwen3_5"
        value={{ ...createEmptyProfileForm(), profileId: 'qwen3_5', displayName: 'Qwen 3.5' }}
        submitting={false}
        onChange={vi.fn()}
        onInsertTemplate={vi.fn()}
        onClose={vi.fn()}
        onSubmit={vi.fn()}
      />,
    );

    expect(screen.getByText('Edit Runtime Profile: qwen3_5')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeInTheDocument();
  });
});
