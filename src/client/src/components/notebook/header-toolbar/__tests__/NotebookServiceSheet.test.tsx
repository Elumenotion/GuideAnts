import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { NotebookServiceSheet } from '../NotebookServiceSheet';

describe('NotebookServiceSheet', () => {
  it('renders nothing when closed', () => {
    const { container } = render(
      <NotebookServiceSheet open={false} onClose={vi.fn()}>
        <div>Content</div>
      </NotebookServiceSheet>
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('renders children when open', () => {
    render(
      <NotebookServiceSheet open onClose={vi.fn()}>
        <div>Sheet body</div>
      </NotebookServiceSheet>
    );
    expect(screen.getByText('Sheet body')).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('calls onClose when backdrop is clicked', () => {
    const onClose = vi.fn();
    render(
      <NotebookServiceSheet open onClose={onClose}>
        <div>Sheet body</div>
      </NotebookServiceSheet>
    );
    fireEvent.mouseDown(screen.getByRole('dialog'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('calls onClose when close button is clicked', () => {
    const onClose = vi.fn();
    render(
      <NotebookServiceSheet open onClose={onClose}>
        <div>Sheet body</div>
      </NotebookServiceSheet>
    );
    fireEvent.click(screen.getByLabelText('Close services'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not close when inner panel is clicked', () => {
    const onClose = vi.fn();
    render(
      <NotebookServiceSheet open onClose={onClose}>
        <div>Sheet body</div>
      </NotebookServiceSheet>
    );
    fireEvent.mouseDown(screen.getByText('Sheet body'));
    expect(onClose).not.toHaveBeenCalled();
  });
});
