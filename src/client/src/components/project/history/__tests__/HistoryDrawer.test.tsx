import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../../../test/test-utils';
import '@testing-library/jest-dom';

// ---- Mock hooks ----
const lineageMock: any = {
  events: [],
  isLoading: false,
  error: null,
  refresh: vi.fn(),
};

vi.mock('../../../../hooks/useFileLineage', () => ({
  useFileLineage: () => lineageMock,
}));

const projectMock: any = { canEdit: false };
vi.mock('../../../../contexts/ProjectContext', async (importOriginal) => {
  const actual: any = await importOriginal();
  return {
    ...actual,
    useProject: () => projectMock,
  } as any;
});

// Stub LineageTimeline so we can assert props
vi.mock('../LineageTimeline', () => ({
  LineageTimeline: (props: any) => (
    <div data-testid="timeline-props">{JSON.stringify(props)}</div>
  ),
}));

import { HistoryDrawer } from '../HistoryDrawer';

describe('HistoryDrawer', () => {
  beforeEach(() => {
    lineageMock.events = [];
    lineageMock.isLoading = false;
    lineageMock.error = null;
    lineageMock.refresh.mockClear();
    projectMock.canEdit = false;
  });

  it('returns null when closed', () => {
    const { container } = render(
      <HistoryDrawer projectId="p1" fileId="f1" isOpen={false} onClose={vi.fn()} />,
    );
    expect(container.firstChild).toBeNull();
  });

  it('shows loading indicator', () => {
    lineageMock.isLoading = true;
    render(<HistoryDrawer projectId="p1" fileId="f1" isOpen onClose={vi.fn()} />);
    expect(screen.getByText('Loading...')).toBeInTheDocument();
  });

  it('shows error message', () => {
    lineageMock.error = 'boom';
    render(<HistoryDrawer projectId="p1" fileId="f1" isOpen onClose={vi.fn()} />);
    expect(screen.getByText('boom')).toBeInTheDocument();
  });
}); 