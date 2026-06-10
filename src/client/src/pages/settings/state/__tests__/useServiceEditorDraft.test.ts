import { describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import {
  applyDraftPatch,
  createInitialServiceEditorDraft,
  switchProviderDraft,
  useServiceEditorDraft,
} from '../useServiceEditorDraft';

interface DraftFields {
  timeoutSeconds?: number;
  endpoint?: string;
}

describe('useServiceEditorDraft state helpers', () => {
  it('preserves per-provider draft values while switching providers', () => {
    let state = createInitialServiceEditorDraft<DraftFields>('provider-a');
    state = applyDraftPatch(state, 'provider-a', { timeoutSeconds: 30 });
    state = switchProviderDraft(state, 'provider-b');
    state = applyDraftPatch(state, 'provider-b', { endpoint: 'http://localhost:8110' });
    state = switchProviderDraft(state, 'provider-a');

    expect(state.activeProviderId).toBe('provider-a');
    expect(state.draftsByProvider['provider-a']).toEqual({ timeoutSeconds: 30 });
    expect(state.draftsByProvider['provider-b']).toEqual({ endpoint: 'http://localhost:8110' });
  });

  it('updates only visible provider draft payload', () => {
    const state = createInitialServiceEditorDraft<DraftFields>('provider-a', {
      'provider-a': { timeoutSeconds: 10 },
      'provider-b': { endpoint: 'https://example.invalid' },
    });

    const next = applyDraftPatch(state, 'provider-a', { timeoutSeconds: 25 });
    expect(next.draftsByProvider['provider-a']).toEqual({ timeoutSeconds: 25 });
    expect(next.draftsByProvider['provider-b']).toEqual({ endpoint: 'https://example.invalid' });
  });

  it('returns the same state when switching to the active provider', () => {
    const state = createInitialServiceEditorDraft<DraftFields>('provider-a', {
      'provider-a': { timeoutSeconds: 10 },
    });
    expect(switchProviderDraft(state, 'provider-a')).toBe(state);
  });
});

describe('useServiceEditorDraft hook', () => {
  it('patches the active draft and tracks dirty providers', () => {
    const { result } = renderHook(() =>
      useServiceEditorDraft<DraftFields>('provider-a', {
        'provider-a': { timeoutSeconds: 10 },
        'provider-b': { endpoint: 'http://localhost:8110' },
      }),
    );

    expect(result.current.activeDraft).toEqual({ timeoutSeconds: 10 });
    expect(result.current.dirtyProvidersExcluding('provider-a')).toEqual(['provider-b']);

    act(() => {
      result.current.patchActiveDraft({ timeoutSeconds: 45 });
    });
    expect(result.current.activeDraft).toEqual({ timeoutSeconds: 45 });

    act(() => {
      result.current.setDraftForProvider('provider-b', { endpoint: 'http://localhost:8120' });
    });
    expect(result.current.draftsByProvider['provider-b']).toEqual({ endpoint: 'http://localhost:8120' });

    act(() => {
      result.current.switchProvider('provider-b');
    });
    expect(result.current.activeProviderId).toBe('provider-b');
    expect(result.current.dirtyProvidersExcluding('provider-b')).toEqual(['provider-a']);
  });
});
