import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { SettingsModal } from '../SettingsModal';

describe('SettingsModal', () => {
  const onClose = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders nothing when closed', () => {
    const { container } = render(
      <SettingsModal isOpen={false} title="Test" onClose={onClose}>
        Body
      </SettingsModal>,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('renders title, children, and footer in a portal', () => {
    render(
      <SettingsModal
        isOpen
        title="Download model"
        onClose={onClose}
        footer={<button type="button">Save</button>}
        maxWidthClass="max-w-4xl"
      >
        <p>Modal body</p>
      </SettingsModal>,
    );

    expect(screen.getByRole('dialog', { name: 'Download model' })).toBeInTheDocument();
    expect(screen.getByText('Modal body')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('Download model');
  });

  it('closes on Escape when dismiss is allowed', () => {
    render(
      <SettingsModal isOpen title="Esc test" onClose={onClose}>
        Body
      </SettingsModal>,
    );

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not close on Escape when disableDismiss is true', () => {
    render(
      <SettingsModal isOpen title="Locked" onClose={onClose} disableDismiss>
        Body
      </SettingsModal>,
    );

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).not.toHaveBeenCalled();
  });

  it('closes when clicking the overlay', () => {
    render(
      <SettingsModal isOpen title="Overlay" onClose={onClose}>
        Body
      </SettingsModal>,
    );

    const dialog = screen.getByRole('dialog', { name: 'Overlay' });
    fireEvent.mouseDown(dialog);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not close when clicking modal content', () => {
    render(
      <SettingsModal isOpen title="Content" onClose={onClose}>
        <p>Inner content</p>
      </SettingsModal>,
    );

    fireEvent.mouseDown(screen.getByText('Inner content'));
    expect(onClose).not.toHaveBeenCalled();
  });

  it('does not close on overlay click when disableOverlayDismiss is true', () => {
    render(
      <SettingsModal isOpen title="No overlay" onClose={onClose} disableOverlayDismiss>
        Body
      </SettingsModal>,
    );

    const dialog = screen.getByRole('dialog', { name: 'No overlay' });
    fireEvent.mouseDown(dialog);
    expect(onClose).not.toHaveBeenCalled();
  });

  it('closes from header button when dismiss is allowed', () => {
    render(
      <SettingsModal isOpen title="Header close" onClose={onClose}>
        Body
      </SettingsModal>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('disables header close button when disableDismiss is true', () => {
    render(
      <SettingsModal isOpen title="Disabled close" onClose={onClose} disableDismiss>
        Body
      </SettingsModal>,
    );

    const closeButton = screen.getByRole('button', { name: 'Close' });
    expect(closeButton).toBeDisabled();
    fireEvent.click(closeButton);
    expect(onClose).not.toHaveBeenCalled();
  });
});
