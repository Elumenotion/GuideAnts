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

  it('hides edit button when canEdit is false', () => {
    const project = buildProject();

    render(
      <ProjectLayout
        project={project}
        sidebar={<div />}
        content={<div />}
        onBack={() => {}}
        canEdit={false}
        onEdit={vi.fn()}
      />,
    );

    expect(screen.queryByRole('button', { name: /edit project/i })).not.toBeInTheDocument();
  });

  it('toggles mobile sidebar from hamburger menu', async () => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });
    const project = buildProject();

    const Sidebar = ({ isMobileOpen }: { isMobileOpen?: boolean }) => (
      <div data-testid="sidebar" data-mobile-open={String(Boolean(isMobileOpen))} />
    );

    render(
      <ProjectLayout
        project={project}
        sidebar={<Sidebar />}
        content={<div data-testid="content">Main Content</div>}
        onBack={() => {}}
        canEdit={false}
      />,
    );

    expect(screen.getByTestId('sidebar')).toHaveAttribute('data-mobile-open', 'false');
    await userEvent.click(screen.getByLabelText('Toggle sidebar'));
    expect(screen.getByTestId('sidebar')).toHaveAttribute('data-mobile-open', 'true');
  });

  it('restores sidebar width from localStorage', () => {
    localStorage.setItem('sidebarWidth', '320');
    localStorage.setItem('sidebarCollapsed', 'true');
    const project = buildProject();

    const Sidebar = ({ defaultWidth, initialCollapsed }: { defaultWidth?: number; initialCollapsed?: boolean }) => (
      <div data-testid="sidebar" data-width={defaultWidth} data-collapsed={String(initialCollapsed)} />
    );

    render(
      <ProjectLayout
        project={project}
        sidebar={<Sidebar />}
        content={<div />}
        onBack={() => {}}
        canEdit={false}
      />,
    );

    expect(screen.getByTestId('sidebar')).toHaveAttribute('data-width', '320');
    expect(screen.getByTestId('sidebar')).toHaveAttribute('data-collapsed', 'true');
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

  it('persists sidebar width and collapse changes from child callbacks', async () => {
    localStorage.removeItem('sidebarWidth');
    localStorage.removeItem('sidebarCollapsed');
    const project = buildProject();

    const Sidebar = ({
      onWidthChange,
      onCollapseChange,
    }: {
      onWidthChange?: (width: number) => void;
      onCollapseChange?: (collapsed: boolean) => void;
    }) => (
      <div>
        <button type="button" onClick={() => onWidthChange?.(288)}>
          Resize
        </button>
        <button type="button" onClick={() => onCollapseChange?.(true)}>
          Collapse
        </button>
      </div>
    );

    render(
      <ProjectLayout
        project={project}
        sidebar={<Sidebar />}
        content={<div />}
        onBack={() => {}}
        canEdit={false}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Resize' }));
    await userEvent.click(screen.getByRole('button', { name: 'Collapse' }));

    expect(localStorage.getItem('sidebarWidth')).toBe('288');
    expect(localStorage.getItem('sidebarCollapsed')).toBe('true');
  });

  it('closes the mobile sidebar from layout callbacks', async () => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });
    const project = buildProject();

    const Sidebar = ({
      isMobileOpen,
      onMobileClose,
    }: {
      isMobileOpen?: boolean;
      onMobileClose?: () => void;
    }) => (
      <div data-testid="sidebar" data-mobile-open={String(Boolean(isMobileOpen))}>
        <button type="button" onClick={() => onMobileClose?.()}>
          Close mobile
        </button>
      </div>
    );

    render(
      <ProjectLayout
        project={project}
        sidebar={<Sidebar />}
        content={<div />}
        onBack={() => {}}
        canEdit={false}
        tourScreenId="notebookView"
        editButtonLabel="Edit notebook"
      />,
    );

    await userEvent.click(screen.getByLabelText('Toggle sidebar'));
    expect(screen.getByTestId('sidebar')).toHaveAttribute('data-mobile-open', 'true');

    await userEvent.click(screen.getByRole('button', { name: 'Close mobile' }));
    expect(screen.getByTestId('sidebar')).toHaveAttribute('data-mobile-open', 'false');
    expect(screen.getByRole('button', { name: 'Start tour' })).toHaveAttribute('data-screen', 'notebookView');
  });

  it('applies custom className to the layout shell', () => {
    const project = buildProject();

    const { container } = render(
      <ProjectLayout
        project={project}
        sidebar={<div />}
        content={<div />}
        onBack={() => {}}
        canEdit={false}
        className="notebook-shell"
      />,
    );

    expect(container.firstChild).toHaveClass('notebook-shell');
  });
}); 