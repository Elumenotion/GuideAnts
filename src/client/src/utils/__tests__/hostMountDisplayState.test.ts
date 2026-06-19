import { describe, expect, it } from 'vitest';
import {
  deriveHostMountDisplayState,
  HOST_MOUNT_DISPLAY_STATE_LABELS,
} from '../hostMountDisplayState';

describe('deriveHostMountDisplayState', () => {
  it('maps pending removal mount status', () => {
    expect(deriveHostMountDisplayState('PendingRemoval', 'Linked', null, null)).toBe('PendingRemoval');
  });

  it('maps link error states', () => {
    expect(deriveHostMountDisplayState('Active', 'LinkError', 'failed', null)).toBe('LinkError');
    expect(deriveHostMountDisplayState('Error', 'PendingRestart', null, null)).toBe('LinkError');
  });

  it('maps missing source from link or mount error message', () => {
    expect(deriveHostMountDisplayState('PendingRestart', 'PendingRestart', 'Missing source', null)).toBe('MissingSource');
    expect(deriveHostMountDisplayState('PendingRestart', null, null, 'Missing source')).toBe('MissingSource');
  });

  it('maps linked state', () => {
    expect(deriveHostMountDisplayState('Active', 'Linked', null, null)).toBe('Linked');
  });

  it('defaults to pending restart', () => {
    expect(deriveHostMountDisplayState('PendingRestart', 'PendingLink', null, null)).toBe('PendingRestart');
  });
});

describe('HOST_MOUNT_DISPLAY_STATE_LABELS', () => {
  it('includes all five display states', () => {
    expect(Object.keys(HOST_MOUNT_DISPLAY_STATE_LABELS).sort()).toEqual([
      'LinkError',
      'Linked',
      'MissingSource',
      'PendingRemoval',
      'PendingRestart',
    ]);
  });
});
