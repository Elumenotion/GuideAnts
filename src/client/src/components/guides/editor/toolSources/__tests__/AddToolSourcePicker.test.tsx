import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { AddToolSourcePicker } from '../AddToolSourcePicker';

describe('AddToolSourcePicker', () => {
  it('shows standard options without advanced', () => {
    render(
      <AddToolSourcePicker isOpen onClose={() => {}} onSelect={() => {}} showAdvanced={false} />
    );
    expect(screen.getByText('Web API')).toBeInTheDocument();
    expect(screen.getByText('Client Actions')).toBeInTheDocument();
    expect(screen.getByText('Sandbox Module')).toBeInTheDocument();
    expect(screen.getByText('MCP Connection')).toBeInTheDocument();
    expect(screen.queryByText('Local Function')).not.toBeInTheDocument();
    expect(screen.queryByText('Raw OpenAPI')).not.toBeInTheDocument();
  });

  it('shows advanced options when enabled', () => {
    render(
      <AddToolSourcePicker isOpen onClose={() => {}} onSelect={() => {}} showAdvanced />
    );
    expect(screen.getByText('Local Function')).toBeInTheDocument();
    expect(screen.getByText('Raw OpenAPI')).toBeInTheDocument();
  });

  it('calls onSelect with kind when option clicked', () => {
    const onSelect = vi.fn();
    render(
      <AddToolSourcePicker isOpen onClose={() => {}} onSelect={onSelect} />
    );
    fireEvent.click(screen.getByTestId('picker-option-client-actions'));
    expect(onSelect).toHaveBeenCalledWith('client-actions');
  });

  it('has dialog with tablist options and closes on cancel', () => {
    const onClose = vi.fn();
    render(
      <AddToolSourcePicker isOpen onClose={onClose} onSelect={() => {}} />
    );
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Cancel'));
    expect(onClose).toHaveBeenCalled();
  });

  it('first option receives focus when opened', async () => {
    render(
      <AddToolSourcePicker isOpen onClose={() => {}} onSelect={() => {}} />
    );
    const firstOption = screen.getByTestId('picker-option-web-api');
    await waitFor(() => {
      expect(document.activeElement).toBe(firstOption);
    });
  });
});
