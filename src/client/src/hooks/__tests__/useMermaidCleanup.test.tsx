import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render } from '../../test/test-utils';
import '@testing-library/jest-dom';

// Hoisted mocking: declare variables that will be initialised inside factory
var setupSpy: any;
var cleanupSpy: any;

vi.mock('../../utils/mermaidCleanup', () => {
  cleanupSpy = vi.fn();
  setupSpy = vi.fn(() => cleanupSpy);
  return { setupMermaidCleanupInterval: setupSpy };
});

import { useMermaidCleanup } from '../useMermaidCleanup';

const TestComp: React.FC<{ enabled?: boolean; interval?: number }> = ({ enabled = true, interval = 1234 }) => {
  useMermaidCleanup(interval, enabled);
  return null;
};

describe('useMermaidCleanup', () => {
  it('registers and cleans up interval when enabled', () => {
    const { unmount } = render(<TestComp />);

    // The hook should have registered the interval with the supplied interval value
    expect(setupSpy).toHaveBeenCalledWith(1234);

    // The cleanup function returned from setup should be called on unmount
    unmount();
    expect(cleanupSpy).toHaveBeenCalled();
  });

  it('does not set up interval when disabled', () => {
    const { unmount } = render(<TestComp enabled={false} />);
    expect(setupSpy).not.toHaveBeenCalled();
    unmount();
    expect(cleanupSpy).not.toHaveBeenCalled();
  });
}); 