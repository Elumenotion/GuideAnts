import React from 'react';
import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { ProjectLayout } from '../ProjectLayout';
import { ProjectDetailsDto } from '../../../types/project';

vi.mock('../../../tour/TourStartButton', () => ({
    TourStartButton: ({ screenId }: { screenId: string }) => <button aria-label="Start tour" data-screen={screenId}>Tour</button>,
}));

// Helper to build a minimal ProjectDetailsDto
const buildProject = (): ProjectDetailsDto => ({
  id: 'project-1',
  title: 'Test Project',
  description: 'A lovely test project',
  created: new Date('2024-01-01T00:00:00Z').toISOString(),
  userRoles: [],
  notebooks: [],
  contentFiles: [],
  folders: [],
  links: [],
  semiStructuredDatas: [],
});

describe('ProjectLayout', () => {
  it('renders header information and handles button clicks', async () => {
    const project = buildProject();
    const onBack = vi.fn();
    const onEdit = vi.fn();

    render(
      <ProjectLayout
        project={project}
        sidebar={<div data-testid="sidebar">Sidebar</div>}
        content={<div data-testid="content">Main Content</div>}
        onBack={onBack}
        canEdit={true}
        onEdit={onEdit}
      />,
    );

    // Title & description
    expect(screen.getByText(project.title)).toBeInTheDocument();
    expect(screen.getByText(project.description)).toBeInTheDocument();

    // Buttons work
    await userEvent.click(screen.getByRole('button', { name: /edit project/i }));
    expect(onEdit).toHaveBeenCalledTimes(1);

    // HomeButton navigates to '/' via useNavigate (no onBack callback)
    expect(screen.getByRole('button', { name: /back to home/i })).toBeInTheDocument();

    // Sidebar and content rendered
    expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    expect(screen.getByTestId('content')).toBeInTheDocument();
  });

  it('respects provided title and subtitle overrides', () => {
    const project = buildProject();

    render(
      <ProjectLayout
        project={project}
        sidebar={<div />}
        content={<div />}
        onBack={() => {}}
        canEdit={false}
        title="Notebook"
        subtitle="My Notebook"
      />,
    );

    expect(screen.getByText('Notebook')).toBeInTheDocument();
    expect(screen.getByText('My Notebook')).toBeInTheDocument();
  });
}); 