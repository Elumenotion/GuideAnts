import { describe, it, expect, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useSidebarSelectionCoordinator } from '../useSidebarSelectionCoordinator';

describe('useSidebarSelectionCoordinator', () => {
  it('activates and deactivates known sections', () => {
    const { result } = renderHook(() =>
      useSidebarSelectionCoordinator({ sections: ['notebooks', 'files', 'links'] })
    );

    expect(result.current.activeSection).toBeNull();
    expect(result.current.isSectionActive('files')).toBe(false);

    act(() => {
      result.current.activateSection('files');
    });

    expect(result.current.activeSection).toBe('files');
    expect(result.current.isSectionActive('files')).toBe(true);
    expect(result.current.isSectionActive('links')).toBe(false);

    act(() => {
      result.current.deactivateSection();
    });

    expect(result.current.activeSection).toBeNull();
  });

  it('warns and ignores unknown section activation', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const { result } = renderHook(() =>
      useSidebarSelectionCoordinator({ sections: ['notebooks'] })
    );

    act(() => {
      result.current.activateSection('unknown');
    });

    expect(warn).toHaveBeenCalledWith('Attempted to activate unknown section: unknown');
    expect(result.current.activeSection).toBeNull();
    warn.mockRestore();
  });
});
