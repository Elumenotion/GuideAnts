import { useState, useCallback, useRef } from 'react';

export interface UseListKeyboardNavigationOptions<T> {
  items: T[];
  getId: (item: T) => string;
  onNavigate?: (id: string) => void;
  onSelectionChange?: (id: string, shiftKey: boolean) => void;
}

export interface UseListKeyboardNavigationReturn {
  focusedId: string | null;
  setFocusedId: (id: string | null) => void;
  handleKeyDown: (event: React.KeyboardEvent) => void;
  /** Ref that indicates focus should be programmatically applied. Check and reset to false after applying. */
  focusIntentRef: React.MutableRefObject<boolean>;
}

export function useListKeyboardNavigation<T>({
  items,
  getId,
  onNavigate,
  onSelectionChange
}: UseListKeyboardNavigationOptions<T>): UseListKeyboardNavigationReturn {
  const [focusedId, setFocusedId] = useState<string | null>(null);
  // Tracks when focus should be programmatically applied (only after explicit user keyboard navigation)
  const focusIntentRef = useRef<boolean>(false);

  const handleKeyDown = useCallback((event: React.KeyboardEvent) => {
    if (items.length === 0) return;

    const currentIndex = focusedId 
      ? items.findIndex(item => getId(item) === focusedId)
      : -1;

    let nextIndex = currentIndex;

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        nextIndex = currentIndex < items.length - 1 ? currentIndex + 1 : currentIndex;
        if (currentIndex === -1) nextIndex = 0;
        break;
      case 'ArrowUp':
        event.preventDefault();
        nextIndex = currentIndex > 0 ? currentIndex - 1 : currentIndex;
        if (currentIndex === -1) nextIndex = items.length - 1;
        break;
      case 'Home':
        event.preventDefault();
        nextIndex = 0;
        break;
      case 'End':
        event.preventDefault();
        nextIndex = items.length - 1;
        break;
      case 'Enter':
        event.preventDefault();
        if (focusedId && onNavigate) {
          onNavigate(focusedId);
        }
        return;
      case ' ': // Space
        // Optional: Space to select without navigating?
        // Standard is often select.
        if (focusedId && onSelectionChange) {
           event.preventDefault();
           onSelectionChange(focusedId, event.shiftKey);
        }
        return;
      default:
        return;
    }

    if (nextIndex !== currentIndex && nextIndex !== -1) {
      const nextId = getId(items[nextIndex]);
      // Signal that focus should be applied programmatically
      focusIntentRef.current = true;
      setFocusedId(nextId);
      
      // If Shift is held, extend selection
      if (event.shiftKey && onSelectionChange) {
        onSelectionChange(nextId, true);
      } else if (!event.shiftKey && !event.ctrlKey && !event.metaKey && onSelectionChange) {
         onSelectionChange(nextId, false);
      }
    }
  }, [items, getId, focusedId, onNavigate, onSelectionChange]);

  return {
    focusedId,
    setFocusedId,
    handleKeyDown,
    focusIntentRef
  };
}

