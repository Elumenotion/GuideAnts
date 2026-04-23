import { useState, useCallback } from 'react';

export interface UseMultiSelectOptions<T> {
  items: T[];
  getId: (item: T) => string;
  onSelectionChange?: (selectedIds: Set<string>) => void;
}

export interface UseMultiSelectReturn<T> {
  selectedIds: Set<string>;
  lastSelectedId: string | null;
  handleClick: (id: string, event: React.MouseEvent) => void;
  selectAll: () => void;
  clearSelection: () => void;
  isSelected: (id: string) => boolean;
  selectedCount: number;
  getSelectedItems: () => T[];
  setSelection: (ids: string[]) => void;
}

export function useMultiSelect<T>({
  items,
  getId,
  onSelectionChange
}: UseMultiSelectOptions<T>): UseMultiSelectReturn<T> {
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [lastSelectedId, setLastSelectedId] = useState<string | null>(null);

  const updateSelection = useCallback((newSelection: Set<string>) => {
    setSelectedIds(newSelection);
    onSelectionChange?.(newSelection);
  }, [onSelectionChange]);

  const isSelected = useCallback((id: string) => {
    return selectedIds.has(id);
  }, [selectedIds]);

  const clearSelection = useCallback(() => {
    if (selectedIds.size > 0) {
      updateSelection(new Set());
      setLastSelectedId(null);
    }
  }, [selectedIds.size, updateSelection]);

  const selectAll = useCallback(() => {
    const allIds = new Set(items.map(getId));
    updateSelection(allIds);
    // lastSelectedId remains unchanged or could be set to the last item? 
    // Usually select all doesn't strictly imply a "last selected" for range select purposes in the same way click does,
    // but setting it to the last item is reasonable.
    if (items.length > 0) {
      setLastSelectedId(getId(items[items.length - 1]));
    }
  }, [items, getId, updateSelection]);

  const setSelection = useCallback((ids: string[]) => {
    const newSet = new Set(ids);
    updateSelection(newSet);
    if (ids.length > 0) {
      setLastSelectedId(ids[ids.length - 1]);
    } else {
      setLastSelectedId(null);
    }
  }, [updateSelection]);

  const handleClick = useCallback((id: string, event: React.MouseEvent) => {
    const { ctrlKey, metaKey, shiftKey } = event;
    const isMultiSelect = ctrlKey || metaKey;
    const isRangeSelect = shiftKey;

    let newSelection = new Set(selectedIds);

    if (isRangeSelect && lastSelectedId) {
      const lastIndex = items.findIndex(item => getId(item) === lastSelectedId);
      const currentIndex = items.findIndex(item => getId(item) === id);

      if (lastIndex !== -1 && currentIndex !== -1) {
        const start = Math.min(lastIndex, currentIndex);
        const end = Math.max(lastIndex, currentIndex);
        
        // If ctrl/cmd is NOT held, clear previous selection first?
        // Standard behavior for Shift+Click usually extends selection from anchor.
        // If no modifier other than shift, it typically resets selection to the range.
        if (!isMultiSelect) {
            newSelection = new Set();
        }

        for (let i = start; i <= end; i++) {
          newSelection.add(getId(items[i]));
        }
      } else {
        // Fallback if index not found
        newSelection.add(id);
        setLastSelectedId(id);
      }
    } else if (isMultiSelect) {
      if (newSelection.has(id)) {
        newSelection.delete(id);
      } else {
        newSelection.add(id);
        setLastSelectedId(id);
      }
    } else {
      // Single click - select only this item
      // Note: Navigate logic is handled by the consumer, here we just handle selection state
      // If the user meant to navigate, they probably didn't hold modifiers.
      // But we always select on click.
      newSelection = new Set([id]);
      setLastSelectedId(id);
    }

    updateSelection(newSelection);
  }, [items, getId, selectedIds, lastSelectedId, updateSelection]);

  const getSelectedItems = useCallback(() => {
    return items.filter(item => selectedIds.has(getId(item)));
  }, [items, selectedIds, getId]);

  return {
    selectedIds,
    lastSelectedId,
    handleClick,
    selectAll,
    clearSelection,
    isSelected,
    selectedCount: selectedIds.size,
    getSelectedItems,
    setSelection
  };
}

