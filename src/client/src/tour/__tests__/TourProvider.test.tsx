import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { TourProvider, useTour } from '../TourProvider';

const driveMock = vi.fn();
const driverFactoryMock = vi.fn(() => ({ drive: driveMock, destroy: vi.fn() }));

vi.mock('driver.js', () => ({
  driver: (...args: unknown[]) => driverFactoryMock(...args),
}));

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <TourProvider>{children}</TourProvider>
);

describe('TourProvider', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('throws when useTour is used outside TourProvider', () => {
    expect(() => renderHook(() => useTour())).toThrow(
      'useTour must be used within TourProvider',
    );
  });

  it('registerTourSteps registers steps and cleanup removes them', () => {
    const { result } = renderHook(() => useTour(), { wrapper });
    const steps = [{ target: '#step-1', content: 'Welcome' }];

    let unregister!: () => void;
    act(() => {
      unregister = result.current.registerTourSteps('home', steps);
    });

    expect(result.current.isRegistered('home')).toBe(true);

    act(() => {
      unregister();
    });

    expect(result.current.isRegistered('home')).toBe(false);
  });

  it('unregister only removes matching step registration', () => {
    const { result } = renderHook(() => useTour(), { wrapper });
    const first = [{ target: '#a', content: 'A' }];
    const second = [{ target: '#b', content: 'B' }];

    let unregisterFirst!: () => void;
    act(() => {
      unregisterFirst = result.current.registerTourSteps('screen', first);
      result.current.registerTourSteps('screen', second);
    });

    act(() => {
      unregisterFirst();
    });

    expect(result.current.isRegistered('screen')).toBe(true);
  });

  it('startTour throws when no tour is registered for the screen', async () => {
    const { result } = renderHook(() => useTour(), { wrapper });

    await expect(result.current.startTour('missing-screen')).rejects.toThrow(
      'No tour registered for screen "missing-screen".',
    );
  });

  it('startTour throws when registered tour has no valid DOM targets', async () => {
    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('empty-targets', [
        { target: '#does-not-exist', content: 'Ghost step' },
      ]);
    });

    await expect(result.current.startTour('empty-targets')).rejects.toThrow(
      'No valid tour targets found for screen "empty-targets".',
    );
  });

  it('startTour keeps dynamic dropdown steps even when target is not yet in the DOM', async () => {
    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('dynamic', [
        { target: '[data-tour-id="menu.context-menu"]', content: 'Menu step' },
      ]);
    });

    await act(async () => {
      await result.current.startTour('dynamic');
    });

    expect(driverFactoryMock).toHaveBeenCalled();
    expect(driveMock).toHaveBeenCalled();
    expect(result.current.isRunning).toBe(true);
    expect(result.current.activeScreenId).toBe('dynamic');
  });

  it('startTour loads driver.js and drives when a static target exists', async () => {
    const target = document.createElement('div');
    target.id = 'tour-target';
    document.body.appendChild(target);

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('project', [
        { target: '#tour-target', content: 'Project overview' },
      ]);
    });

    await act(async () => {
      await result.current.startTour('project');
    });

    expect(driverFactoryMock).toHaveBeenCalled();
    expect(driveMock).toHaveBeenCalled();
    expect(result.current.isRunning).toBe(true);
    expect(result.current.activeScreenId).toBe('project');

    document.body.removeChild(target);
  });

  it('startTour throws when registered tour has empty step list', async () => {
    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('empty', []);
    });

    await expect(result.current.startTour('empty')).rejects.toThrow(
      'No tour registered for screen "empty".',
    );
  });

  it('onDestroyed clears running state via driver callback', async () => {
    const onDeselect = vi.fn();
    const target = document.createElement('div');
    target.id = 'destroy-target';
    document.body.appendChild(target);

    let destroyedHandler: (() => void) | undefined;
    let popoverRender: ((popover: unknown, options: { state: { activeIndex: number } }) => Promise<void>) | undefined;
    driverFactoryMock.mockImplementationOnce((config: {
      onDestroyed?: () => void;
      steps: Array<{ popover: { onPopoverRender: typeof popoverRender } }>;
    }) => {
      destroyedHandler = config.onDestroyed;
      popoverRender = config.steps[0]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('cleanup', [
        { target: '#destroy-target', content: 'Step', onDeselect },
      ]);
    });

    await act(async () => {
      await result.current.startTour('cleanup');
    });

    expect(result.current.isRunning).toBe(true);

    await act(async () => {
      await popoverRender?.({}, { state: { activeIndex: 0 } });
    });

    act(() => {
      destroyedHandler?.();
    });

    expect(onDeselect).toHaveBeenCalled();
    expect(result.current.isRunning).toBe(false);
    expect(result.current.activeScreenId).toBeUndefined();

    document.body.removeChild(target);
  });

  it('invokes onHighlight for static steps through driver popover hook', async () => {
    const onHighlight = vi.fn();
    const target = document.createElement('div');
    target.id = 'highlight-target';
    document.body.appendChild(target);

    let popoverRender: ((popover: unknown, options: { state: { activeIndex: number } }) => Promise<void>) | undefined;
    driverFactoryMock.mockImplementationOnce((config: { steps: Array<{ popover: { onPopoverRender: typeof popoverRender } }> }) => {
      popoverRender = config.steps[0]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('highlight', [
        { target: '#highlight-target', content: 'Highlighted', onHighlight },
      ]);
    });

    await act(async () => {
      await result.current.startTour('highlight');
    });

    await act(async () => {
      await popoverRender?.({}, { state: { activeIndex: 0 } });
    });

    expect(onHighlight).toHaveBeenCalled();

    document.body.removeChild(target);
  });

  it('applies popover offset transforms for static steps', async () => {
    const target = document.createElement('div');
    target.id = 'offset-target';
    document.body.appendChild(target);

    let popoverRender: ((popover: { wrapper: HTMLElement }, options: { state: { activeIndex: number } }) => Promise<void>) | undefined;
    driverFactoryMock.mockImplementationOnce((config: {
      steps: Array<{ popover: { onPopoverRender: typeof popoverRender } }>;
    }) => {
      popoverRender = config.steps[0]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('offset', [
        {
          target: '#offset-target',
          content: 'Offset step',
          popoverOffset: 12,
          popoverAlignOffset: 4,
        },
      ]);
    });

    await act(async () => {
      await result.current.startTour('offset');
    });

    const wrapperEl = document.createElement('div');
    await act(async () => {
      await popoverRender?.({ wrapper: wrapperEl }, { state: { activeIndex: 0 } });
    });

    expect(wrapperEl.style.transform).toContain('12px');

    document.body.removeChild(target);
  });

  it('dispatches close-context-menus when leaving a context-menu step', async () => {
    const onDeselect = vi.fn();
    const dispatchSpy = vi.spyOn(window, 'dispatchEvent');

    let popoverRender: ((popover: unknown, options: { state: { activeIndex: number } }) => Promise<void>) | undefined;
    driverFactoryMock.mockImplementationOnce((config: { steps: Array<{ popover: { onPopoverRender: typeof popoverRender } }> }) => {
      popoverRender = config.steps[0]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('menu', [
        {
          target: '[data-tour-id="sidebar.context-menu"]',
          content: 'Menu',
          onDeselect,
        },
      ]);
    });

    await act(async () => {
      await result.current.startTour('menu');
    });

    await act(async () => {
      await popoverRender?.({}, { state: { activeIndex: 0 } });
      await popoverRender?.({}, { state: { activeIndex: 1 } });
    });

    expect(dispatchSpy).toHaveBeenCalledWith(expect.objectContaining({ type: 'close-context-menus' }));

    dispatchSpy.mockRestore();
  });
});
