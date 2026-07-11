import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { LlamaCuratedModelPicker } from '../LlamaCuratedModelPicker';
import type { LlamaCatalogDefinitionDto } from '../../../../types/settings';
import { catalogFixture } from '../fixtures';

const models = (catalogFixture as { models: LlamaCatalogDefinitionDto[] }).models;

describe('LlamaCuratedModelPicker', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders searchable cards and selects a model', () => {
    const onSelect = vi.fn();
    render(
      <LlamaCuratedModelPicker
        models={models}
        searchQuery=""
        selectedDefinitionId={null}
        loading={false}
        error={null}
        onSearchChange={vi.fn()}
        onSelect={onSelect}
        onRetry={vi.fn()}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /Qwen 3.6 35B A3B MTP/i }));
    expect(onSelect).toHaveBeenCalledWith('qwen3.6-35b-a3b-mtp');
  });

  it('filters by search query through parent state contract', () => {
    render(
      <LlamaCuratedModelPicker
        models={models}
        searchQuery="vision"
        selectedDefinitionId={null}
        loading={false}
        error={null}
        onSearchChange={vi.fn()}
        onSelect={vi.fn()}
        onRetry={vi.fn()}
      />
    );

    expect(screen.getByText(/Qwen 3.6 35B A3B/i)).toBeInTheDocument();
    expect(screen.queryByText(/MTP/i)).not.toBeInTheDocument();
  });
});
