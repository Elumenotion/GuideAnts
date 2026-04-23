import React from 'react';
import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { SidebarContainer } from '../SidebarContainer';

describe('SidebarContainer', () => {
  it('toggles collapsed state when button clicked', async () => {
    const onCollapseChange = vi.fn();
    render(
      <SidebarContainer onCollapseChange={onCollapseChange}>
        <div>Sidebar Content</div>
      </SidebarContainer>
    );

    // Initially should show collapse button
    const collapseBtn = screen.getByTitle(/collapse sidebar/i);
    expect(collapseBtn).toBeInTheDocument();

    await userEvent.click(collapseBtn);
    expect(onCollapseChange).toHaveBeenCalledWith(true);

    // Button title should now be Expand after collapse
    const expandBtn = screen.getByTitle(/expand sidebar/i);
    expect(expandBtn).toBeInTheDocument();
  });

  it('calls onWidthChange during drag resize', async () => {
    const onWidthChange = vi.fn();
    const { container } = render(
      <SidebarContainer onWidthChange={onWidthChange}>
        <div>Resizable Sidebar</div>
      </SidebarContainer>
    );

    const handle = container.querySelector('div.cursor-col-resize') as HTMLElement;
    expect(handle).toBeTruthy();

    // Start dragging
    fireEvent.mouseDown(handle);
    fireEvent.mouseMove(document, { clientX: 400 });
    fireEvent.mouseUp(document);

    expect(onWidthChange).toHaveBeenCalled();
    // Ensure the width passed is within expected range
    const passedWidth = onWidthChange.mock.calls[0][0] as number;
    expect(passedWidth).toBeGreaterThanOrEqual(200);
    expect(passedWidth).toBeLessThanOrEqual(600);
  });
}); 