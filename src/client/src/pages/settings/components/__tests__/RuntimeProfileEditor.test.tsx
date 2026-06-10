import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { RuntimeProfileEditor } from '../RuntimeProfileEditor';
import { createEmptyProfileForm } from '../../utils';

describe('RuntimeProfileEditor', () => {
  it('renders core profile fields and calls onChange', () => {
    const onChange = vi.fn();
    const value = {
      ...createEmptyProfileForm(),
      profileId: 'my-profile',
      displayName: 'My Profile',
      providers: ['openai-chat'],
    };

    render(<RuntimeProfileEditor mode="full" value={value} onChange={onChange} />);

    expect(screen.getByDisplayValue('my-profile')).toBeInTheDocument();
    expect(screen.getByDisplayValue('My Profile')).toBeInTheDocument();

    fireEvent.change(screen.getByDisplayValue('My Profile'), { target: { value: 'Renamed' } });
    expect(onChange).toHaveBeenCalledWith('displayName', 'Renamed');
  });

  it('shows llama-cpp-only fields and template buttons for local providers', () => {
    const onInsertTemplate = vi.fn();
    const value = {
      ...createEmptyProfileForm(),
      profileId: 'local-profile',
      providers: ['llama-cpp'],
    };

    render(
      <RuntimeProfileEditor
        mode="inline"
        value={value}
        onChange={vi.fn()}
        onInsertTemplate={onInsertTemplate}
      />
    );

    expect(screen.getByLabelText(/Combine System and Developer Messages/i)).toBeInTheDocument();
    expect(screen.getByText(/Thought Block Pattern/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Insert qwen3_5/i }));
    expect(onInsertTemplate).toHaveBeenCalledWith('qwen3_5');
  });

  it('hides llama-cpp-only fields for non-local providers', () => {
    const value = {
      ...createEmptyProfileForm(),
      providers: ['openai-chat'],
    };

    render(
      <RuntimeProfileEditor
        mode="full"
        value={value}
        onChange={vi.fn()}
        onInsertTemplate={vi.fn()}
      />
    );

    expect(screen.queryByLabelText(/Combine System and Developer Messages/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Insert qwen3_5/i })).not.toBeInTheDocument();
  });

  it('renders submit button and disables identity fields when requested', () => {
    const onSubmit = vi.fn();
    const value = {
      ...createEmptyProfileForm(),
      profileId: 'locked-id',
      providers: ['openai-chat'],
    };

    render(
      <RuntimeProfileEditor
        mode="full"
        value={value}
        onChange={vi.fn()}
        onSubmit={onSubmit}
        submitLabel="Create profile"
        disableIdentityFields
      />
    );

    expect(screen.getByRole('button', { name: /Create profile/i })).toBeInTheDocument();
    expect(screen.getByDisplayValue('locked-id')).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: /Create profile/i }));
    expect(onSubmit).toHaveBeenCalledTimes(1);
  });

  it('updates every editable field and all template buttons in full mode', () => {
    const onChange = vi.fn();
    const onInsertTemplate = vi.fn();
    const value = {
      ...createEmptyProfileForm(),
      profileId: 'profile-a',
      displayName: 'Profile A',
      description: 'Desc',
      providers: ['llama-cpp'],
      combineSystemAndDeveloperMessages: false,
      thoughtBlockPattern: '.*',
      samplingParametersJson: '{"temperature":0.7}',
      thinkingControlJson: '{}',
    };

    render(
      <RuntimeProfileEditor
        mode="full"
        value={value}
        onChange={onChange}
        onInsertTemplate={onInsertTemplate}
        onSubmit={vi.fn()}
        submitting
        submitLabel="Saving"
      />
    );

    fireEvent.change(screen.getByDisplayValue('profile-a'), { target: { value: 'profile-b' } });
    fireEvent.change(screen.getByDisplayValue('Profile A'), { target: { value: 'Profile B' } });
    fireEvent.change(screen.getByDisplayValue('Desc'), { target: { value: 'New desc' } });
    fireEvent.click(screen.getByLabelText(/Combine System and Developer Messages/i));
    fireEvent.change(screen.getByDisplayValue('.*'), { target: { value: '<think>.*</think>' } });
    fireEvent.change(screen.getByDisplayValue('{"temperature":0.7}'), { target: { value: '{}' } });
    fireEvent.change(screen.getByDisplayValue('{}'), { target: { value: '{"choiceActions":{}}' } });
    fireEvent.click(screen.getByRole('button', { name: /Insert qwen3_6/i }));
    fireEvent.click(screen.getByRole('button', { name: /Insert gemma4/i }));

    expect(onChange).toHaveBeenCalledWith('profileId', 'profile-b');
    expect(onChange).toHaveBeenCalledWith('displayName', 'Profile B');
    expect(onChange).toHaveBeenCalledWith('description', 'New desc');
    expect(onChange).toHaveBeenCalledWith('combineSystemAndDeveloperMessages', true);
    expect(onChange).toHaveBeenCalledWith('thoughtBlockPattern', '<think>.*</think>');
    expect(onChange).toHaveBeenCalledWith('samplingParametersJson', '{}');
    expect(onChange).toHaveBeenCalledWith('thinkingControlJson', '{"choiceActions":{}}');
    expect(onInsertTemplate).toHaveBeenCalledWith('qwen3_6');
    expect(onInsertTemplate).toHaveBeenCalledWith('gemma4');
    expect(screen.getByRole('button', { name: 'Saving' })).toBeDisabled();
  });
});
