import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { fireEvent, render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ProfilesTab } from '../ProfilesTab';
import { createEmptyProfileForm } from '../../utils';
import type { SettingsRuntimeProfileDto } from '../../../../types/settings';

vi.mock('../RuntimeProfileDialog', () => ({
  RuntimeProfileDialog: () => <div data-testid="profile-dialog">profile-dialog</div>,
}));

const profile: SettingsRuntimeProfileDto = {
  profileId: 'local-llama',
  displayName: 'Local Llama',
  description: 'Dev profile',
  providers: ['llama-cpp'],
  created: '2026-01-01T00:00:00Z',
  updated: '2026-01-02T00:00:00Z',
  combineSystemAndDeveloperMessages: false,
  thoughtBlockPattern: '',
  samplingParametersJson: '{}',
  thinkingControlJson: '{}',
};

function renderProfiles(overrides: Partial<React.ComponentProps<typeof ProfilesTab>> = {}) {
  const props = {
    profileDialogOpen: false,
    editingProfileId: null,
    profileForm: createEmptyProfileForm(),
    profileSaving: false,
    profilesLoading: false,
    profilesError: null,
    profiles: [profile],
    deletingProfileId: null,
    onProfileFormChange: vi.fn(),
    onOpenCreateProfile: vi.fn(),
    onImportProfile: vi.fn(),
    onResetProfileForm: vi.fn(),
    onSaveProfile: vi.fn(),
    onRetryLoadProfiles: vi.fn(),
    onEditProfile: vi.fn(),
    onRequestDeleteProfile: vi.fn(),
    onInsertTemplate: vi.fn(),
    ...overrides,
  };

  return { ...render(<ProfilesTab {...props} />), props };
}

describe('ProfilesTab', () => {
  it('renders profiles and wires row actions', async () => {
    const user = userEvent.setup();
    const { props } = renderProfiles();

    expect(screen.getByText('Local Llama')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Add Profile/i }));
    expect(props.onOpenCreateProfile).toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    expect(props.onEditProfile).toHaveBeenCalledWith(profile);

    await user.click(screen.getByRole('button', { name: 'Delete' }));
    expect(props.onRequestDeleteProfile).toHaveBeenCalledWith('local-llama');
  });

  it('imports a runtime profile JSON file', async () => {
    const onImportProfile = vi.fn();
    renderProfiles({ onImportProfile });

    const file = new File(
      [JSON.stringify({
        profileId: 'imported',
        displayName: 'Imported',
        providers: ['openai-chat'],
        combineSystemAndDeveloperMessages: false,
        samplingParametersJson: '{}',
        thinkingControlJson: '{}',
      })],
      'profile.json',
      { type: 'application/json' },
    );

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [file] } });

    await screen.findByTestId('profile-dialog');
    expect(onImportProfile).toHaveBeenCalledWith(
      expect.objectContaining({ profileId: 'imported', displayName: 'Imported' }),
    );
  });

  it('shows retry affordance when profile loading fails', async () => {
    const user = userEvent.setup();
    const onRetryLoadProfiles = vi.fn();
    renderProfiles({ profilesError: 'Network down', onRetryLoadProfiles });

    await user.click(screen.getByRole('button', { name: /Retry/i }));
    expect(onRetryLoadProfiles).toHaveBeenCalled();
  });

  it('shows loading and empty states', () => {
    const loadingProps = {
      profileDialogOpen: false,
      editingProfileId: null,
      profileForm: createEmptyProfileForm(),
      profileSaving: false,
      profilesLoading: true,
      profilesError: null,
      profiles: [] as SettingsRuntimeProfileDto[],
      deletingProfileId: null,
      onProfileFormChange: vi.fn(),
      onOpenCreateProfile: vi.fn(),
      onImportProfile: vi.fn(),
      onResetProfileForm: vi.fn(),
      onSaveProfile: vi.fn(),
      onRetryLoadProfiles: vi.fn(),
      onEditProfile: vi.fn(),
      onRequestDeleteProfile: vi.fn(),
      onInsertTemplate: vi.fn(),
    };

    const { rerender } = render(<ProfilesTab {...loadingProps} />);
    expect(screen.getByText(/Loading runtime profiles/i)).toBeInTheDocument();

    rerender(<ProfilesTab {...loadingProps} profilesLoading={false} />);
    expect(screen.getByText(/No runtime profiles configured yet/i)).toBeInTheDocument();
  });

  it('exports a profile JSON file', async () => {
    const user = userEvent.setup();
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:profile');

    renderProfiles();
    await user.click(screen.getByRole('button', { name: 'Export' }));

    expect(clickSpy).toHaveBeenCalled();
    expect(revoke).toHaveBeenCalledWith('blob:profile');

    clickSpy.mockRestore();
    revoke.mockRestore();
  });

  it('shows import validation errors for invalid JSON', async () => {
    renderProfiles();

    const file = new File(['not-json'], 'profile.json', { type: 'application/json' });
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [file] } });

    expect(await screen.findByText(/not valid JSON/i)).toBeInTheDocument();
  });

  it('shows deleting spinner on the active profile row', () => {
    renderProfiles({ deletingProfileId: 'local-llama' });
    expect(screen.getByRole('button', { name: 'Delete' })).toBeDisabled();
  });

  it('renders empty provider badges and llama provider styling', () => {
    renderProfiles({
      profiles: [
        {
          ...profile,
          profileId: 'empty-providers',
          displayName: 'Empty',
          providers: [],
        },
        {
          ...profile,
          profileId: 'llama-only',
          displayName: 'Llama',
          providers: ['llama-cpp', 'openai-chat'],
        },
      ],
    });

    expect(screen.getByText('—')).toBeInTheDocument();
    expect(screen.getByText('llama-cpp')).toHaveClass('text-amber-700');
    expect(screen.getByText('openai-chat')).toHaveClass('text-blue-700');
  });
});
