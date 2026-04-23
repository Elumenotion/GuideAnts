import React from 'react';
// @ts-ignore - add electron to window for tests
declare global {
  interface Window { electron: any }
}
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { render } from '../../test/test-utils';
import ZoomControl from '../ZoomControl';
// Access the mocked electron API
const electronMock = window.electron as any;

/**
 * Helper to dispatch browser events that ZoomControl listens to.
 */
const fireWheel = (options: WheelEventInit) => {
  window.dispatchEvent(new WheelEvent('wheel', options));
};

const fireKey = (options: KeyboardEventInit) => {
  window.dispatchEvent(new KeyboardEvent('keydown', options));
};

describe('ZoomControl', () => {
  beforeEach(() => {
    // Reset zoom before each test so assertions are clean
    const rootElement = document.getElementById('root');
    if (rootElement) {
      rootElement.style.transform = '';
      rootElement.style.transformOrigin = '';
    }
  });

  afterEach(() => {
    // Ensure we leave no global listener side-effects for other tests
    const rootElement = document.getElementById('root');
    if (rootElement) {
      rootElement.style.transform = '';
      rootElement.style.transformOrigin = '';
    }
  });

  it('initialises zoom to 100% using Electron API', () => {
    render(<ZoomControl />);
    expect(electronMock.setZoom).toHaveBeenCalledWith(1);
  });

  it('zooms in and out using mouse wheel with Ctrl/Meta held', () => {
    render(<ZoomControl />);
    electronMock.setZoom.mockClear();
    // Zoom in
    fireWheel({ ctrlKey: true, deltaY: -100 });
    expect(electronMock.setZoom).toHaveBeenCalled();
    // Zoom out
    fireWheel({ ctrlKey: true, deltaY: 100 });
    expect(electronMock.setZoom).toHaveBeenCalled();
  });

  it('handles keyboard shortcuts (+ / - / 0) when Ctrl/Meta held', () => {
    render(<ZoomControl />);
    electronMock.setZoom.mockClear();
    fireKey({ key: '=', ctrlKey: true });
    fireKey({ key: '-', ctrlKey: true });
    fireKey({ key: '0', ctrlKey: true });
    // should have been called at least 3 times
    expect(electronMock.setZoom).toHaveBeenCalledTimes(3);
  });
}); 