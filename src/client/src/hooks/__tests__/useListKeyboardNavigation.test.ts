import { describe, it, expect, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useListKeyboardNavigation } from '../useListKeyboardNavigation';

interface Item {
  id: string;
  label: string;
}

const items: Item[] = [
  { id: 'a', label: 'Alpha' },
  { id: 'b', label: 'Beta' },
  { id: 'c', label: 'Gamma' },
];

const getId = (item: Item) => item.id;

const keyEvent = (key: string, extras: Partial<React.KeyboardEvent> = {}) =>
  ({
    key,
    preventDefault: vi.fn(),
    shiftKey: false,
    ctrlKey: false,
    metaKey: false,
    ...extras,
  }) as unknown as React.KeyboardEvent;

describe('useListKeyboardNavigation', () => {
  it('does nothing when items list is empty', () => {
    const { result } = renderHook(() =>
      useListKeyboardNavigation({ items: [], getId })
    );

    act(() => {
      result.current.handleKeyDown(keyEvent('ArrowDown'));
    });

    expect(result.current.focusedId).toBeNull();
  });

  it('moves focus down from unset to first item', () => {
    const { result } = renderHook(() =>
      useListKeyboardNavigation({ items, getId })
    );

    act(() => {
      result.current.handleKeyDown(keyEvent('ArrowDown'));
    });

    expect(result.current.focusedId).toBe('a');
    expect(result.current.focusIntentRef.current).toBe(true);
  });

  it('moves focus up from unset to last item', () => {
    const { result } = renderHook(() =>
      useListKeyboardNavigation({ items, getId })
    );

    act(() => {
      result.current.handleKeyDown(keyEvent('ArrowUp'));
    });

    expect(result.current.focusedId).toBe('c');
  });

  it('navigates with Home and End keys', () => {
    const { result } = renderHook(() =>
      useListKeyboardNavigation({ items, getId })
    );

    act(() => {
      result.current.setFocusedId('b');
      result.current.handleKeyDown(keyEvent('Home'));
    });
    expect(result.current.focusedId).toBe('a');

    act(() => {
      result.current.handleKeyDown(keyEvent('End'));
    });
    expect(result.current.focusedId).toBe('c');
  });

  it('calls onNavigate on Enter', () => {
    const onNavigate = vi.fn();
    const { result } = renderHook(() =>
      useListKeyboardNavigation({ items, getId, onNavigate })
    );

    act(() => {
      result.current.setFocusedId('b');
    });
    act(() => {
      result.current.handleKeyDown(keyEvent('Enter'));
    });

    expect(onNavigate).toHaveBeenCalledWith('b');
  });

  it('calls onSelectionChange on Space', () => {
    const onSelectionChange = vi.fn();
    const { result } = renderHook(() =>
      useListKeyboardNavigation({ items, getId, onSelectionChange })
    );

    act(() => {
      result.current.setFocusedId('a');
    });
    act(() => {
      result.current.handleKeyDown(keyEvent(' ', { shiftKey: true }));
    });

    expect(onSelectionChange).toHaveBeenCalledWith('a', true);
  });

  it('extends selection with shift+arrow', () => {
    const onSelectionChange = vi.fn();
    const { result } = renderHook(() =>
      useListKeyboardNavigation({ items, getId, onSelectionChange })
    );

    act(() => {
      result.current.setFocusedId('a');
    });
    act(() => {
      result.current.handleKeyDown(keyEvent('ArrowDown', { shiftKey: true }));
    });

    expect(result.current.focusedId).toBe('b');
    expect(onSelectionChange).toHaveBeenCalledWith('b', true);
  });

  it('selects without shift on arrow when onSelectionChange provided', () => {
    const onSelectionChange = vi.fn();
    const { result } = renderHook(() =>
      useListKeyboardNavigation({ items, getId, onSelectionChange })
    );

    act(() => {
      result.current.setFocusedId('a');
    });
    act(() => {
      result.current.handleKeyDown(keyEvent('ArrowDown'));
    });

    expect(onSelectionChange).toHaveBeenCalledWith('b', false);
  });
});
