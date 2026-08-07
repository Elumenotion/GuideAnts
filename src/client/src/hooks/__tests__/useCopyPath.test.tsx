import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useCopyPath } from '../useCopyPath';
import { ToastProvider } from '../../components/common/Toast';

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <ToastProvider>{children}</ToastProvider>
);

describe('useCopyPath', () => {
  let writeText: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
  });

  it('copies a single path as root-relative', async () => {
    const { result } = renderHook(() => useCopyPath(), { wrapper });

    await act(async () => {
      await result.current.copyPaths(['docs/api.md']);
    });

    expect(writeText).toHaveBeenCalledWith('/docs/api.md');
  });

  it('does not double-prefix a path that already starts with "/"', async () => {
    const { result } = renderHook(() => useCopyPath(), { wrapper });

    await act(async () => {
      await result.current.copyPaths(['/already/rooted']);
    });

    expect(writeText).toHaveBeenCalledWith('/already/rooted');
  });

  it('joins multiple paths with newlines, each made root-relative', async () => {
    const { result } = renderHook(() => useCopyPath(), { wrapper });

    await act(async () => {
      await result.current.copyPaths(['a', 'b/c']);
    });

    expect(writeText).toHaveBeenCalledWith('/a\n/b/c');
  });

  it('filters out empty paths before copying', async () => {
    const { result } = renderHook(() => useCopyPath(), { wrapper });

    await act(async () => {
      await result.current.copyPaths(['', 'real']);
    });

    expect(writeText).toHaveBeenCalledWith('/real');
  });

  it('does nothing when every path is empty', async () => {
    const { result } = renderHook(() => useCopyPath(), { wrapper });

    await act(async () => {
      await result.current.copyPaths(['']);
    });

    expect(writeText).not.toHaveBeenCalled();
  });
});
