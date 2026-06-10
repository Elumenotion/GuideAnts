import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { HeaderActionsBar } from '../HeaderActionsBar';

function ActionButton({ label }: { label: string }) {
  return (
    <button type="button" className="px-4">
      {label}
    </button>
  );
}

describe('HeaderActionsBar', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('renders all action buttons when layout width is sufficient', () => {
    render(
      <HeaderActionsBar>
        <ActionButton label="One" />
        <ActionButton label="Two" />
      </HeaderActionsBar>,
    );

    expect(screen.getByRole('button', { name: 'One' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Two' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'More actions' })).not.toBeInTheDocument();
  });

  it('collapses trailing buttons into the overflow menu when space is tight', async () => {
    const user = userEvent.setup();
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (
      this: Element,
    ) {
      const width = this.tagName === 'SPAN' ? 80 : 0;
      return {
        width,
        height: 32,
        top: 0,
        left: 0,
        right: width,
        bottom: 32,
        x: 0,
        y: 0,
        toJSON: () => ({}),
      } as DOMRect;
    });

    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (
      this: HTMLElement,
    ) {
      if (this.getAttribute('class')?.includes('gap-1')) {
        return 120;
      }
      return 0;
    });

    render(
      <HeaderActionsBar variant="header">
        <ActionButton label="Alpha" />
        <ActionButton label="Beta" />
        <ActionButton label="Gamma" />
      </HeaderActionsBar>,
    );

    const moreButton = await screen.findByRole('button', { name: 'More actions' });
    expect(moreButton).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Gamma' })).not.toBeInTheDocument();

    await user.click(moreButton);
    expect(screen.getByRole('menu')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Gamma' })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  it('closes the overflow menu on outside click', async () => {
    const user = userEvent.setup();
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (
      this: Element,
    ) {
      const width = this.tagName === 'SPAN' ? 100 : 0;
      return {
        width,
        height: 32,
        top: 0,
        left: 0,
        right: width,
        bottom: 32,
        x: 0,
        y: 0,
        toJSON: () => ({}),
      } as DOMRect;
    });
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (
      this: HTMLElement,
    ) {
      if (this.getAttribute('class')?.includes('gap-1')) {
        return 100;
      }
      return 0;
    });

    render(
      <div>
        <HeaderActionsBar variant="toolbar">
          <ActionButton label="First" />
          <ActionButton label="Second" />
        </HeaderActionsBar>
        <button type="button">Outside</button>
      </div>,
    );

    const moreButton = await screen.findByRole('button', { name: 'More actions' });
    await user.click(moreButton);
    expect(screen.getByRole('menu')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Outside' }));
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  it('does not place toolbar dividers in the overflow menu', async () => {
    const user = userEvent.setup();
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (
      this: Element,
    ) {
      const width = this.tagName === 'SPAN' ? 90 : 0;
      return {
        width,
        height: 32,
        top: 0,
        left: 0,
        right: width,
        bottom: 32,
        x: 0,
        y: 0,
        toJSON: () => ({}),
      } as DOMRect;
    });
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (
      this: HTMLElement,
    ) {
      if (this.getAttribute('class')?.includes('gap-1')) {
        return 110;
      }
      return 0;
    });

    render(
      <HeaderActionsBar>
        <ActionButton label="Keep" />
        <span className="toolbar-divider" />
        <ActionButton label="Overflow" />
      </HeaderActionsBar>,
    );

    const moreButton = await screen.findByRole('button', { name: 'More actions' });
    await user.click(moreButton);

    const menu = screen.getByRole('menu');
    expect(menu.querySelector('.toolbar-divider')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Overflow' })).toBeInTheDocument();
  });
});
