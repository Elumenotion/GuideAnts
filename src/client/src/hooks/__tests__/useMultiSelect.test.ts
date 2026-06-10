import { describe, it, expect, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useMultiSelect } from '../useMultiSelect';

interface Item {
  id: string;
  name: string;
}

const items: Item[] = [
  { id: '1', name: 'One' },
  { id: '2', name: 'Two' },
  { id: '3', name: 'Three' },
  { id: '4', name: 'Four' },
];

const getId = (item: Item) => item.id;

const click = (modifiers: Partial<React.MouseEvent> = {}) =>
  ({
    ctrlKey: false,
    metaKey: false,
    shiftKey: false,
    ...modifiers,
  }) as unknown as React.MouseEvent;

describe('useMultiSelect', () => {
  it('selects a single item on plain click', () => {
    const onSelectionChange = vi.fn();
    const { result } = renderHook(() =>
      useMultiSelect({ items, getId, onSelectionChange })
    );

    act(() => {
      result.current.handleClick('2', click());
    });

    expect(result.current.isSelected('2')).toBe(true);
    expect(result.current.selectedCount).toBe(1);
    expect(result.current.lastSelectedId).toBe('2');
    expect(onSelectionChange).toHaveBeenCalledWith(new Set(['2']));
  });

  it('toggles items with ctrl+click', () => {
    const { result } = renderHook(() => useMultiSelect({ items, getId }));

    act(() => {
      result.current.handleClick('1', click());
    });
    act(() => {
      result.current.handleClick('2', click({ ctrlKey: true }));
    });
    act(() => {
      result.current.handleClick('1', click({ ctrlKey: true }));
    });

    expect(result.current.isSelected('1')).toBe(false);
    expect(result.current.isSelected('2')).toBe(true);
    expect(result.current.selectedCount).toBe(1);
  });

  it('selects a range with shift+click', () => {
    const { result } = renderHook(() => useMultiSelect({ items, getId }));

    act(() => {
      result.current.handleClick('1', click());
    });
    act(() => {
      result.current.handleClick('3', click({ shiftKey: true }));
    });

    expect(result.current.selectedIds).toEqual(new Set(['1', '2', '3']));
    expect(result.current.getSelectedItems().map(getId)).toEqual(['1', '2', '3']);
  });

  it('extends range with ctrl+shift+click', () => {
    const { result } = renderHook(() => useMultiSelect({ items, getId }));

    act(() => {
      result.current.handleClick('1', click());
    });
    act(() => {
      result.current.handleClick('2', click({ ctrlKey: true }));
    });
    act(() => {
      result.current.handleClick('4', click({ shiftKey: true, ctrlKey: true }));
    });

    expect(result.current.isSelected('1')).toBe(true);
    expect(result.current.isSelected('2')).toBe(true);
    expect(result.current.isSelected('3')).toBe(true);
    expect(result.current.isSelected('4')).toBe(true);
  });

  it('selectAll selects every item', () => {
    const { result } = renderHook(() => useMultiSelect({ items, getId }));

    act(() => {
      result.current.selectAll();
    });

    expect(result.current.selectedCount).toBe(4);
    expect(result.current.lastSelectedId).toBe('4');
  });

  it('clearSelection empties selection', () => {
    const { result } = renderHook(() => useMultiSelect({ items, getId }));

    act(() => {
      result.current.selectAll();
    });
    act(() => {
      result.current.clearSelection();
    });

    expect(result.current.selectedCount).toBe(0);
    expect(result.current.lastSelectedId).toBeNull();
  });

  it('setSelection replaces current selection', () => {
    const { result } = renderHook(() => useMultiSelect({ items, getId }));

    act(() => {
      result.current.setSelection(['2', '4']);
    });

    expect(result.current.selectedIds).toEqual(new Set(['2', '4']));
    expect(result.current.lastSelectedId).toBe('4');
  });

  it('clearSelection is a no-op when already empty', () => {
    const onSelectionChange = vi.fn();
    const { result } = renderHook(() =>
      useMultiSelect({ items, getId, onSelectionChange })
    );

    act(() => {
      result.current.clearSelection();
    });

    expect(onSelectionChange).not.toHaveBeenCalled();
  });
});
