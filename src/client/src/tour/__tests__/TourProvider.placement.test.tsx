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

type PopoverRender = (
  popover: { wrapper?: HTMLElement },
  options: { state: { activeIndex: number } }
) => Promise<void>;

describe('TourProvider – placement & dropdown', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    document.querySelectorAll('.driver-popover').forEach((el) => el.remove());
    document.querySelectorAll('[data-tour-id]').forEach((el) => el.remove());
    document.querySelectorAll('#static-next').forEach((el) => el.remove());
  });

  it('positions dropdown step relative to assistant selector button', async () => {
    vi.useFakeTimers();

    const button = document.createElement('div');
    button.setAttribute('data-tour-id', 'conversation.assistant.selector');
    document.body.appendChild(button);
    Object.defineProperty(button, 'getBoundingClientRect', {
      value: () => ({ right: 400, bottom: 120, left: 200, top: 80, width: 200, height: 40 }),
    });

    const popover = document.createElement('div');
    popover.className = 'driver-popover';
    document.body.appendChild(popover);

    const onHighlight = vi.fn(() => {
      const panel = document.createElement('div');
      panel.setAttribute('data-tour-id', 'conversation.assistant.dropdown');
      document.body.appendChild(panel);
    });

    let popoverRender: PopoverRender | undefined;
    driverFactoryMock.mockImplementationOnce((config: {
      steps: Array<{ popover: { onPopoverRender: PopoverRender } }>;
    }) => {
      popoverRender = config.steps[0]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('dropdown', [
        {
          target: '[data-tour-id="conversation.assistant.dropdown"]',
          content: 'Pick assistant',
          popoverOffset: 0,
          onHighlight,
        },
      ]);
    });

    await act(async () => {
      await result.current.startTour('dropdown');
    });

    await act(async () => {
      const pending = popoverRender?.({}, { state: { activeIndex: 0 } });
      await vi.advanceTimersByTimeAsync(300);
      await pending;
    });

    expect(onHighlight).toHaveBeenCalled();
    expect(popover.style.inset).toContain('px');

    vi.useRealTimers();
  });

  it('warns when dynamic context-menu element never appears', async () => {
    vi.useFakeTimers();
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

    const popover = document.createElement('div');
    popover.className = 'driver-popover';
    document.body.appendChild(popover);

    let popoverRender: PopoverRender | undefined;
    driverFactoryMock.mockImplementationOnce((config: {
      steps: Array<{ popover: { onPopoverRender: PopoverRender } }>;
    }) => {
      popoverRender = config.steps[0]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('missing', [
        {
          target: '[data-tour-id="never.context-menu"]',
          content: 'Ghost',
          onHighlight: () => {},
        },
      ]);
    });

    await act(async () => {
      await result.current.startTour('missing');
    });

    await act(async () => {
      const pending = popoverRender?.({}, { state: { activeIndex: 0 } });
      await vi.advanceTimersByTimeAsync(2500);
      await pending;
    });

    expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('never.context-menu'));
    warnSpy.mockRestore();
    vi.useRealTimers();
  });

  it('calls onDeselect when leaving a dropdown step', async () => {
    const onDeselect = vi.fn();
    const dropdown = document.createElement('div');
    dropdown.setAttribute('data-tour-id', 'settings.dropdown');
    document.body.appendChild(dropdown);

    const next = document.createElement('div');
    next.id = 'static-next';
    document.body.appendChild(next);

    let firstRender: PopoverRender | undefined;
    let secondRender: PopoverRender | undefined;
    driverFactoryMock.mockImplementationOnce((config: {
      steps: Array<{ popover: { onPopoverRender: PopoverRender } }>;
    }) => {
      firstRender = config.steps[0]?.popover.onPopoverRender;
      secondRender = config.steps[1]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('dropdown-leave', [
        {
          target: '[data-tour-id="settings.dropdown"]',
          content: 'Dropdown',
          onDeselect,
        },
        { target: '#static-next', content: 'Next' },
      ]);
    });

    await act(async () => {
      await result.current.startTour('dropdown-leave');
    });

    await act(async () => {
      await firstRender?.({}, { state: { activeIndex: 0 } });
      await secondRender?.({}, { state: { activeIndex: 1 } });
    });

    expect(onDeselect).toHaveBeenCalled();
  });

  it('reuses existing context menu element without reopening', async () => {
    vi.useFakeTimers();

    const menu = document.createElement('div');
    menu.setAttribute('data-tour-id', 'sidebar.context-menu');
    document.body.appendChild(menu);

    const popover = document.createElement('div');
    popover.className = 'driver-popover';
    document.body.appendChild(popover);

    const onHighlight = vi.fn();

    let popoverRender: PopoverRender | undefined;
    driverFactoryMock.mockImplementationOnce((config: {
      steps: Array<{ popover: { onPopoverRender: PopoverRender } }>;
    }) => {
      popoverRender = config.steps[0]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('existing-menu', [
        {
          target: '[data-tour-id="sidebar.context-menu"]',
          content: 'Menu',
          placement: 'left',
          onHighlight,
        },
      ]);
    });

    await act(async () => {
      await result.current.startTour('existing-menu');
    });

    await act(async () => {
      const pending = popoverRender?.({}, { state: { activeIndex: 0 } });
      await vi.advanceTimersByTimeAsync(100);
      await pending;
    });

    expect(onHighlight).not.toHaveBeenCalled();
    expect((menu as HTMLElement).style.zIndex).toBe('10001');

    vi.useRealTimers();
  });

  it('positions context menu popover with left placement', async () => {
    vi.useFakeTimers();

    const rootFolder = document.createElement('div');
    rootFolder.setAttribute('data-tour-id', 'notebook.folder.root');
    document.body.appendChild(rootFolder);
    Object.defineProperty(rootFolder, 'getBoundingClientRect', {
      value: () => ({ left: 100, top: 50, right: 250, bottom: 90, width: 150, height: 40 }),
    });

    const popover = document.createElement('div');
    popover.className = 'driver-popover';
    Object.defineProperty(popover, 'getBoundingClientRect', {
      value: () => ({ width: 200, height: 80, left: 0, top: 0, right: 200, bottom: 80 }),
    });
    document.body.appendChild(popover);

    const onHighlight = vi.fn(() => {
      const menu = document.createElement('div');
      menu.setAttribute('data-tour-id', 'notebook.context-menu');
      document.body.appendChild(menu);
      Object.defineProperty(menu, 'getBoundingClientRect', {
        value: () => ({ right: 320, top: 50, left: 260, bottom: 150, width: 60, height: 100 }),
      });
    });

    let popoverRender: PopoverRender | undefined;
    driverFactoryMock.mockImplementationOnce((config: {
      steps: Array<{ popover: { onPopoverRender: PopoverRender } }>;
    }) => {
      popoverRender = config.steps[0]?.popover.onPopoverRender;
      return { drive: driveMock, destroy: vi.fn() };
    });

    const { result } = renderHook(() => useTour(), { wrapper });

    act(() => {
      result.current.registerTourSteps('left-placement', [
        {
          target: '[data-tour-id="notebook.context-menu"]',
          content: 'Left popover',
          placement: 'left',
          popoverOffset: 5,
          popoverAlignOffset: 2,
          onHighlight,
        },
      ]);
    });

    await act(async () => {
      await result.current.startTour('left-placement');
    });

    await act(async () => {
      const pending = popoverRender?.({}, { state: { activeIndex: 0 } });
      await vi.advanceTimersByTimeAsync(300);
      await pending;
    });

    expect(onHighlight).toHaveBeenCalled();
    expect(popover.style.inset).toContain('px');

    vi.useRealTimers();
  });
});
