import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render } from '../test/test-utils';
import ZoomControl from './ZoomControl';

// shared helpers exposed by preload in real app
interface ElectronAPI {
  getZoom: () => number;
  setZoom: (z: number) => void;
}

declare global {
  interface Window {
    electron?: ElectronAPI;
  }
}

describe('ZoomControl', () => {
  let getZoomMock: ReturnType<typeof vi.fn>;
  let setZoomMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    getZoomMock = vi.fn().mockReturnValue(1);
    setZoomMock = vi.fn();
    // @ts-ignore – test stub
    window.electron = { getZoom: getZoomMock, setZoom: setZoomMock };
  });

  afterEach(() => {
    // @ts-ignore
    window.electron = undefined;
  });

  it('initialises zoom to 100%', () => {
    render(<ZoomControl />);
    expect(setZoomMock).toHaveBeenCalledWith(1);
  });

  it('increments and decrements zoom with Ctrl + mouse wheel', () => {
    render(<ZoomControl />);
    // simulate ctrl wheel up (negative deltaY -> zoom in)
    const wheelUp = new WheelEvent('wheel', { deltaY: -100, ctrlKey: true });
    window.dispatchEvent(wheelUp);
    expect(setZoomMock).toHaveBeenLastCalledWith(1 + 0.33);

    // simulate ctrl wheel down -> zoom out
    getZoomMock.mockReturnValue(1.33);
    const wheelDown = new WheelEvent('wheel', { deltaY: 100, ctrlKey: true });
    window.dispatchEvent(wheelDown);
    expect(setZoomMock).toHaveBeenLastCalledWith(1);
  });

  it('resets zoom with Ctrl + 0', () => {
    getZoomMock.mockReturnValue(2);
    render(<ZoomControl />);
    const event = new KeyboardEvent('keydown', { key: '0', ctrlKey: true });
    window.dispatchEvent(event);
    expect(setZoomMock).toHaveBeenLastCalledWith(1);
  });
}); 