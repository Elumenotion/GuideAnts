import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useSidebarKeyboardShortcuts } from '../useSidebarKeyboardShortcuts';

describe('useSidebarKeyboardShortcuts', () => {
  const handlers = {
    onDelete: vi.fn(),
    onRename: vi.fn(),
    onSelectAll: vi.fn(),
    onClearSelection: vi.fn(),
    onCopy: vi.fn(),
    onPaste: vi.fn(),
  };

  const dispatchKey = (
    key: string,
    target: EventTarget = document.body,
    extras: Partial<KeyboardEventInit> = {}
  ) => {
    const event = new KeyboardEvent('keydown', {
      key,
      bubbles: true,
      cancelable: true,
      ...extras,
    });
    Object.defineProperty(event, 'target', { value: target });
    window.dispatchEvent(event);
    return event;
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('does not register handlers when inactive', () => {
    renderHook(() =>
      useSidebarKeyboardShortcuts({ ...handlers, isActive: false })
    );

    dispatchKey('Delete');
    expect(handlers.onDelete).not.toHaveBeenCalled();
  });

  it('triggers delete on Delete key', () => {
    renderHook(() =>
      useSidebarKeyboardShortcuts({ ...handlers, isActive: true })
    );

    const event = dispatchKey('Delete');
    expect(handlers.onDelete).toHaveBeenCalledTimes(1);
    expect(event.defaultPrevented).toBe(true);
  });

  it('triggers rename on F2', () => {
    renderHook(() =>
      useSidebarKeyboardShortcuts({ ...handlers, isActive: true })
    );

    dispatchKey('F2');
    expect(handlers.onRename).toHaveBeenCalledTimes(1);
  });

  it('triggers select all on Ctrl+A', () => {
    renderHook(() =>
      useSidebarKeyboardShortcuts({ ...handlers, isActive: true })
    );

    dispatchKey('a', document.body, { ctrlKey: true });
    expect(handlers.onSelectAll).toHaveBeenCalledTimes(1);
  });

  it('triggers clear selection on Escape', () => {
    renderHook(() =>
      useSidebarKeyboardShortcuts({ ...handlers, isActive: true })
    );

    dispatchKey('Escape');
    expect(handlers.onClearSelection).toHaveBeenCalledTimes(1);
  });

  it('triggers copy and paste shortcuts', () => {
    renderHook(() =>
      useSidebarKeyboardShortcuts({ ...handlers, isActive: true })
    );

    dispatchKey('c', document.body, { ctrlKey: true });
    dispatchKey('v', document.body, { ctrlKey: true });

    expect(handlers.onCopy).toHaveBeenCalledTimes(1);
    expect(handlers.onPaste).toHaveBeenCalledTimes(1);
  });

  it('ignores shortcuts when typing in an input', () => {
    renderHook(() =>
      useSidebarKeyboardShortcuts({ ...handlers, isActive: true })
    );

    const input = document.createElement('input');
    dispatchKey('Delete', input);
    dispatchKey('a', input, { ctrlKey: true });

    expect(handlers.onDelete).not.toHaveBeenCalled();
    expect(handlers.onSelectAll).not.toHaveBeenCalled();
  });

  it('ignores shortcuts when typing in contenteditable', () => {
    renderHook(() =>
      useSidebarKeyboardShortcuts({ ...handlers, isActive: true })
    );

    const editable = document.createElement('div');
    editable.contentEditable = 'true';
    Object.defineProperty(editable, 'isContentEditable', { value: true });
    dispatchKey('F2', editable);

    expect(handlers.onRename).not.toHaveBeenCalled();
  });

  it('does not trigger Backspace delete', () => {
    renderHook(() =>
      useSidebarKeyboardShortcuts({ ...handlers, isActive: true })
    );

    dispatchKey('Backspace');
    expect(handlers.onDelete).not.toHaveBeenCalled();
  });
});
