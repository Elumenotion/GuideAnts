import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
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

describe('TourProvider – dynamic steps', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    document.querySelectorAll('.driver-popover').forEach((el) => el.remove());
  });

  it('waits for dynamic context menu element and positions popover', async () => {
    vi.useFakeTimers();

    const onHighlight = vi.fn(() => {
      const menu = document.createElement('div');
      menu.setAttribute('data-tour-id', 'sidebar.context-menu');
      document.body.appendChild(menu);
      Object.defineProperty(menu, 'getBoundingClientRect', {
        value: () => ({ right: 300, top: 50, left: 200, bottom: 150, width: 100, height: 100 }),
      });
    });

    const rootFolder = document.createElement('div');
    rootFolder.setAttribute('data-tour-id', 'sidebar.folder.root');
    document.body.appendChild(rootFolder);
    Object.defineProperty(rootFolder, 'getBoundingClientRect', {
      value: () => ({ right: 180, top: 40, left: 20, bottom: 80, width: 160, height: 40 }),
    });

    const popover = document.createElement('div');
    popover.className = 'driver-popover';
    document.body.appendChild(popover);

    let popoverRender: ((popover: unknown, options: { state: { activeIndex: number } }) => Promise<void>) | undefined;
    driverFactoryMock.mockImplementationOnce((config: {
      steps: Array<{ popover: { onPopoverRender: typeof popoverRender } }>;
    }) => {
      popoverRender = config.steps[0]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('context-menu', [
        {
          target: '[data-tour-id="sidebar.context-menu"]',
          content: 'Context menu step',
          placement: 'right',
          onHighlight,
        },
      ]);
    });

    await act(async () => {
      await result.current.startTour('context-menu');
    });

    await act(async () => {
      const renderPromise = popoverRender?.({}, { state: { activeIndex: 0 } });
      await vi.advanceTimersByTimeAsync(300);
      await renderPromise;
    });

    expect(onHighlight).toHaveBeenCalled();
    expect(document.querySelector('[data-tour-id="sidebar.context-menu"]')).toBeTruthy();

    rootFolder.remove();
    popover.remove();
    document.querySelector('[data-tour-id="sidebar.context-menu"]')?.remove();
    vi.useRealTimers();
  });

  it('opens context menu dynamically when target is missing initially', async () => {
    vi.useFakeTimers();

    const onHighlight = vi.fn(() => {
      const menu = document.createElement('div');
      menu.setAttribute('data-tour-id', 'sidebar.context-menu');
      document.body.appendChild(menu);
    });

    const rootFolder = document.createElement('div');
    rootFolder.setAttribute('data-tour-id', 'sidebar.folder.root');
    document.body.appendChild(rootFolder);
    Object.defineProperty(rootFolder, 'getBoundingClientRect', {
      value: () => ({ right: 200, top: 40, left: 20, bottom: 80, width: 180, height: 40 }),
    });

    const popover = document.createElement('div');
    popover.className = 'driver-popover';
    document.body.appendChild(popover);

    let popoverRender: ((popover: unknown, options: { state: { activeIndex: number } }) => Promise<void>) | undefined;
    driverFactoryMock.mockImplementationOnce((config: {
      steps: Array<{ popover: { onPopoverRender: typeof popoverRender } }>;
    }) => {
      popoverRender = config.steps[0]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('dynamic-menu', [
        {
          target: '[data-tour-id="sidebar.context-menu"]',
          content: 'Open menu',
          placement: 'bottom',
          onHighlight,
        },
      ]);
    });

    await act(async () => {
      await result.current.startTour('dynamic-menu');
    });

    await act(async () => {
      const pending = popoverRender?.({}, { state: { activeIndex: 0 } });
      await vi.advanceTimersByTimeAsync(300);
      await pending;
    });

    expect(onHighlight).toHaveBeenCalled();
    expect(document.querySelector('[data-tour-id="sidebar.context-menu"]')).toBeTruthy();

    rootFolder.remove();
    popover.remove();
    document.querySelector('[data-tour-id="sidebar.context-menu"]')?.remove();
    vi.useRealTimers();
  });

  it('invokes onDeselect from onDestroyed for the active step', async () => {
    const onDeselect = vi.fn();
    const target = document.createElement('div');
    target.id = 'destroy-step';
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
      result.current.registerTourSteps('destroy', [
        { target: '#destroy-step', content: 'Step', onDeselect },
      ]);
    });

    await act(async () => {
      await result.current.startTour('destroy');
    });

    await act(async () => {
      await popoverRender?.({}, { state: { activeIndex: 0 } });
    });

    act(() => {
      destroyedHandler?.();
    });

    expect(onDeselect).toHaveBeenCalled();
    expect(result.current.isRunning).toBe(false);

    target.remove();
  });
});
