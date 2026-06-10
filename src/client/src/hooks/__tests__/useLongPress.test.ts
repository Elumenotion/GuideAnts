import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useLongPress } from '../useLongPress';

describe('useLongPress', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const createTouchEvent = (
    type: string,
    clientX: number,
    clientY: number,
    overrides: Partial<React.TouchEvent> = {}
  ) =>
    ({
      touches: [{ clientX, clientY }],
      preventDefault: vi.fn(),
      ...overrides,
    }) as unknown as React.TouchEvent;

  it('triggers onLongPress after threshold', () => {
    const onLongPress = vi.fn();
    const onPressStart = vi.fn();
    const { result } = renderHook(() =>
      useLongPress({ onLongPress, onPressStart, threshold: 500 })
    );

    act(() => {
      result.current.onTouchStart(createTouchEvent('touchstart', 10, 20));
    });

    expect(onPressStart).toHaveBeenCalledTimes(1);
    expect(onLongPress).not.toHaveBeenCalled();

    act(() => {
      vi.advanceTimersByTime(500);
    });

    expect(onLongPress).toHaveBeenCalledWith({ clientX: 10, clientY: 20 });
  });

  it('cancels long press on touch end before threshold', () => {
    const onLongPress = vi.fn();
    const onPressEnd = vi.fn();
    const { result } = renderHook(() =>
      useLongPress({ onLongPress, onPressEnd, threshold: 500 })
    );

    act(() => {
      result.current.onTouchStart(createTouchEvent('touchstart', 5, 5));
      result.current.onTouchEnd(createTouchEvent('touchend', 5, 5));
    });

    act(() => {
      vi.advanceTimersByTime(500);
    });

    expect(onLongPress).not.toHaveBeenCalled();
    expect(onPressEnd).toHaveBeenCalled();
  });

  it('cancels long press when finger moves beyond moveThreshold', () => {
    const onLongPress = vi.fn();
    const onPressEnd = vi.fn();
    const { result } = renderHook(() =>
      useLongPress({ onLongPress, onPressEnd, threshold: 500, moveThreshold: 10 })
    );

    act(() => {
      result.current.onTouchStart(createTouchEvent('touchstart', 0, 0));
      result.current.onTouchMove(createTouchEvent('touchmove', 15, 0));
    });

    act(() => {
      vi.advanceTimersByTime(500);
    });

    expect(onLongPress).not.toHaveBeenCalled();
    expect(onPressEnd).toHaveBeenCalled();
  });

  it('prevents default on touch end when long press fired', () => {
    const onLongPress = vi.fn();
    const { result } = renderHook(() =>
      useLongPress({ onLongPress, threshold: 300 })
    );

    act(() => {
      result.current.onTouchStart(createTouchEvent('touchstart', 1, 2));
      vi.advanceTimersByTime(300);
    });

    const endEvent = createTouchEvent('touchend', 1, 2);
    act(() => {
      result.current.onTouchEnd(endEvent);
    });

    expect(endEvent.preventDefault).toHaveBeenCalled();
  });

  it('does nothing when disabled', () => {
    const onLongPress = vi.fn();
    const onPressStart = vi.fn();
    const { result } = renderHook(() =>
      useLongPress({ onLongPress, onPressStart, disabled: true })
    );

    act(() => {
      result.current.onTouchStart(createTouchEvent('touchstart', 0, 0));
      vi.advanceTimersByTime(1000);
    });

    expect(onPressStart).not.toHaveBeenCalled();
    expect(onLongPress).not.toHaveBeenCalled();
  });

  it('clears timer on touch cancel', () => {
    const onLongPress = vi.fn();
    const onPressEnd = vi.fn();
    const { result } = renderHook(() =>
      useLongPress({ onLongPress, onPressEnd, threshold: 500 })
    );

    act(() => {
      result.current.onTouchStart(createTouchEvent('touchstart', 0, 0));
      result.current.onTouchCancel(createTouchEvent('touchcancel', 0, 0));
      vi.advanceTimersByTime(500);
    });

    expect(onLongPress).not.toHaveBeenCalled();
    expect(onPressEnd).toHaveBeenCalled();
  });
});
