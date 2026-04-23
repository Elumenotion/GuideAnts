import { describe, expect, it } from 'vitest';
import {
  applyDraftPatch,
  createInitialServiceEditorDraft,
  switchProviderDraft,
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
});
