import React from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import { NotebookLayout } from '../NotebookLayout';
import { ProjectDetailsDto, NotebookTemplateDto } from '../../../types/project';
import { NotebookDetailsDto } from '../../../types/notebook';

// Mock TwoColumnLayout
vi.mock('../TwoColumnLayout', () => ({
    TwoColumnLayout: ({ leftColumn, rightColumn }: any) => (
        <div data-testid="two-column-layout">
            <div data-testid="left-column">{leftColumn}</div>
            <div data-testid="right-column">{rightColumn}</div>
        </div>
    ),
}));

describe('NotebookLayout', () => {
    const mockProject: ProjectDetailsDto = {
        id: 'project-1',
        title: 'Test Project',
        description: 'Test Description',
        created: '2023-01-01T00:00:00Z',
        userRoles: [],
        notebooks: [],
        contentFiles: [],
        folders: [],
        links: [],
        semiStructuredDatas: [],
    };

    const mockNotebook: NotebookDetailsDto = {
        id: 'notebook-1',
        title: 'Test Notebook',
        projectId: 'project-1',
        cells: [],
        created: '2023-01-01T00:00:00Z',
        modified: '2023-01-01T00:00:00Z',
        createdBy: 'user-1',
        modifiedBy: 'user-1',
    };

    const mockTemplate: NotebookTemplateDto = {
        id: 'template-1',
        templateName: 'Code Notebook',
        description: 'A template for coding tasks',
        avatarUrl: '/api/notebook-templates/avatar/Code%20Notebook',
        conversationStarters: ['How can I help you code?', 'What would you like to build?'],
    };

    const defaultProps = {
        project: mockProject,
        notebook: mockNotebook,
        sidebar: <div data-testid="mock-sidebar">Sidebar Content</div>,
        content: <div data-testid="mock-content">Content Area</div>,
        onBack: vi.fn(),
        canEdit: true,
        onEdit: vi.fn(),
    };

    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('renders notebook header with title only', () => {
        render(<NotebookLayout {...defaultProps} />);

        expect(screen.getByText('Test Notebook')).toBeInTheDocument();
        expect(screen.getByText('Back to Project')).toBeInTheDocument();
        
        // Should not show created date or project reference in header
        expect(screen.queryByText(/Created on/)).not.toBeInTheDocument();
        expect(screen.queryByText('Notebook in Test Project')).not.toBeInTheDocument();
    });

    it('renders template icon when template is provided', () => {
        render(<NotebookLayout {...defaultProps} notebookTemplate={mockTemplate} />);
        
        const avatar = screen.getByAltText('Code Notebook');
        expect(avatar).toBeInTheDocument();
        expect(avatar).toHaveAttribute('src', expect.stringContaining('/api/notebook-templates/avatar/Code%20Notebook'));
        expect(avatar.parentElement).toHaveAttribute('title', 'Code Notebook - A template for coding tasks');
    });

    it('does not render template icon when no template is provided', () => {
        render(<NotebookLayout {...defaultProps} />);

        expect(screen.queryByAltText('Code Notebook')).not.toBeInTheDocument();
    });

    it('shows edit button when canEdit is true and onEdit is provided', () => {
        render(<NotebookLayout {...defaultProps} canEdit={true} onEdit={vi.fn()} />);

        expect(screen.getByText('Edit Notebook')).toBeInTheDocument();
    });

    it('hides edit button when canEdit is false', () => {
        render(<NotebookLayout {...defaultProps} canEdit={false} onEdit={vi.fn()} />);

        expect(screen.queryByText('Edit Notebook')).not.toBeInTheDocument();
    });

    it('hides edit button when onEdit is not provided', () => {
        render(<NotebookLayout {...defaultProps} canEdit={true} onEdit={undefined} />);

        expect(screen.queryByText('Edit Notebook')).not.toBeInTheDocument();
    });

    it('calls onBack when back button is clicked', async () => {
        const onBack = vi.fn();
        render(<NotebookLayout {...defaultProps} onBack={onBack} />);

        const backButton = screen.getByText('Back to Project');
        await userEvent.click(backButton);

        expect(onBack).toHaveBeenCalledTimes(1);
    });

    it('calls onEdit when edit button is clicked', async () => {
        const onEdit = vi.fn();
        render(<NotebookLayout {...defaultProps} onEdit={onEdit} />);

        const editButton = screen.getByText('Edit Notebook');
        await userEvent.click(editButton);

        expect(onEdit).toHaveBeenCalledTimes(1);
    });

    it('renders sidebar and content in TwoColumnLayout', () => {
        render(<NotebookLayout {...defaultProps} />);

        expect(screen.getByTestId('two-column-layout')).toBeInTheDocument();
        expect(screen.getByTestId('mock-sidebar')).toBeInTheDocument();
        expect(screen.getByTestId('mock-content')).toBeInTheDocument();
    });

    it('renders headerCenter when provided', () => {
        render(
            <NotebookLayout
                {...defaultProps}
                headerCenter={<span data-testid="center-toolbar">Toolbar</span>}
            />
        );
        expect(screen.getByTestId('notebook-header-center')).toBeInTheDocument();
        expect(screen.getByTestId('center-toolbar')).toBeInTheDocument();
    });

    it('handles template with no avatar gracefully', () => {
        const templateWithoutAvatar = { ...mockTemplate, avatarUrl: '' };
        render(<NotebookLayout {...defaultProps} notebookTemplate={templateWithoutAvatar} />);

        // When there's no avatar, no template icon should be displayed
        expect(screen.queryByAltText('Code Notebook')).not.toBeInTheDocument();
    });

    it('handles template with conversation starters in data but not in UI', () => {
        const templateWithOneStarter = { ...mockTemplate, conversationStarters: ['Single starter'] };
        render(<NotebookLayout {...defaultProps} notebookTemplate={templateWithOneStarter} />);

        // Conversation starters should not be displayed in the UI anymore
        expect(screen.queryByText('1 starter')).not.toBeInTheDocument();
        expect(screen.queryByText('2 starters')).not.toBeInTheDocument();
    });



    it('handles template with no conversation starters', () => {
        const templateWithNoStarters = { ...mockTemplate, conversationStarters: [] };
        render(<NotebookLayout {...defaultProps} notebookTemplate={templateWithNoStarters} />);

        expect(screen.queryByText(/starter/)).not.toBeInTheDocument();
    });
}); 