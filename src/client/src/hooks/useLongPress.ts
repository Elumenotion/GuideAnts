import { useCallback, useRef } from 'react';

export interface LongPressEvent {
  clientX: number;
  clientY: number;
}

export interface UseLongPressOptions {
  /** Duration in ms before long press triggers (default: 500) */
  threshold?: number;
  /** Maximum movement in pixels before cancelling (default: 10) */
  moveThreshold?: number;
  /** Callback when long press is detected */
  onLongPress: (event: LongPressEvent) => void;
  /** Optional callback when press starts */
  onPressStart?: () => void;
  /** Optional callback when press ends or is cancelled */
  onPressEnd?: () => void;
  /** Disable the long press behavior */
  disabled?: boolean;
}

export interface UseLongPressReturn {
  onTouchStart: (e: React.TouchEvent) => void;
  onTouchEnd: (e: React.TouchEvent) => void;
  onTouchMove: (e: React.TouchEvent) => void;
  onTouchCancel: (e: React.TouchEvent) => void;
}

/**
 * Hook for detecting long press (press and hold) gestures on touch devices.
 * Used as mobile equivalent of right-click context menus.
 * 
 * @example
 * const longPressHandlers = useLongPress({
 *   onLongPress: (e) => showContextMenu(e.clientX, e.clientY),
 *   threshold: 500,
 * });
 * 
 * <button {...longPressHandlers} onClick={handleClick}>
 *   Press and hold for options
 * </button>
 */
export function useLongPress({
  threshold = 500,
  moveThreshold = 10,
  onLongPress,
  onPressStart,
  onPressEnd,
  disabled = false,
}: UseLongPressOptions): UseLongPressReturn {
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const startPosRef = useRef<{ x: number; y: number } | null>(null);
  const longPressTriggeredRef = useRef(false);

  const clearTimer = useCallback(() => {
    if (timerRef.current) {
      clearTimeout(timerRef.current);
      timerRef.current = null;
    }
  }, []);

  const onTouchStart = useCallback((e: React.TouchEvent) => {
    if (disabled) return;

    const touch = e.touches[0];
    if (!touch) return;

    startPosRef.current = { x: touch.clientX, y: touch.clientY };
    longPressTriggeredRef.current = false;

    onPressStart?.();

    timerRef.current = setTimeout(() => {
      longPressTriggeredRef.current = true;
      onLongPress({
        clientX: startPosRef.current?.x ?? touch.clientX,
        clientY: startPosRef.current?.y ?? touch.clientY,
      });
    }, threshold);
  }, [disabled, threshold, onLongPress, onPressStart]);

  const onTouchEnd = useCallback((e: React.TouchEvent) => {
    clearTimer();
    onPressEnd?.();

    // If long press was triggered, prevent the subsequent click/tap
    if (longPressTriggeredRef.current) {
      e.preventDefault();
      longPressTriggeredRef.current = false;
    }

    startPosRef.current = null;
  }, [clearTimer, onPressEnd]);

  const onTouchMove = useCallback((e: React.TouchEvent) => {
    if (!startPosRef.current) return;

    const touch = e.touches[0];
    if (!touch) return;

    const deltaX = Math.abs(touch.clientX - startPosRef.current.x);
    const deltaY = Math.abs(touch.clientY - startPosRef.current.y);

    // Cancel if moved beyond threshold (user is scrolling/dragging)
    if (deltaX > moveThreshold || deltaY > moveThreshold) {
      clearTimer();
      startPosRef.current = null;
      onPressEnd?.();
    }
  }, [moveThreshold, clearTimer, onPressEnd]);

  const onTouchCancel = useCallback((_e: React.TouchEvent) => {
    clearTimer();
    startPosRef.current = null;
    longPressTriggeredRef.current = false;
    onPressEnd?.();
  }, [clearTimer, onPressEnd]);

  return {
    onTouchStart,
    onTouchEnd,
    onTouchMove,
    onTouchCancel,
  };
}

