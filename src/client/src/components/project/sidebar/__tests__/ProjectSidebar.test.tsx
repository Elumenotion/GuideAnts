import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '../../../../test/test-utils';
import { ProjectSidebar } from '../ProjectSidebar';
import { ProjectDetailsDto } from '../../../../types/project';
import userEvent from '@testing-library/user-event';

const sampleProject: ProjectDetailsDto = {
  id: 'p1',
  title: 'Demo',
  description: '',
  created: '',
  userRoles: [],
  notebooks: [ { id: 'n1', title: 'Notebook 1'} ],
  contentFiles: [],
  links: [ { id: 'l1', url: 'https://example.com' } ],
  folders: [],
  semiStructuredDatas: [],
};

describe('ProjectSidebar', () => {
  it('calls onSectionToggle when collapse button clicked', async () => {
    const toggleSpy = vi.fn();
    render(
      <ProjectSidebar
        project={sampleProject}
        expandedSections={new Set(['contentFiles'])}
        selectedItem={null}
        onSectionToggle={toggleSpy}
        onItemSelect={vi.fn()}
      />
    );

    const collapseBtn = screen.getByLabelText('Collapse section');
    await userEvent.click(collapseBtn);
    expect(toggleSpy).toHaveBeenCalledWith('contentFiles');
  });

  it('highlights selected item and calls onItemSelect', async () => {
    const selectSpy = vi.fn();
    render(
      <ProjectSidebar
        project={sampleProject}
        expandedSections={new Set(['notebooks'])}
        selectedItem={null}
        onSectionToggle={vi.fn()}
        onItemSelect={selectSpy}
      />
    );

    const notebookButton = await screen.findByText('Notebook 1');
    await userEvent.click(notebookButton);
    expect(selectSpy).toHaveBeenCalledWith('notebooks', 'n1');
  });

  it('invokes onAddTeamMember when add project member button is clicked', async () => {
    const addTeamSpy = vi.fn();
    render(
      <ProjectSidebar
        project={{
          ...sampleProject,
          userRoles: [{ userId: 'u1', userName: 'Alice', userEmail: 'alice@example.com', roleName: 'Owner' }],
        }}
        expandedSections={new Set(['projectMembers'])}
        selectedItem={null}
        onSectionToggle={vi.fn()}
        onItemSelect={vi.fn()}
        onAddTeamMember={addTeamSpy}
        isCurrentUserOwner
      />
    );

    // Title becomes "Add new project member" for project member sections
    const addTeamBtn = await screen.findByTitle('Add new project member');
    await userEvent.click(addTeamBtn);

    expect(addTeamSpy).toHaveBeenCalledTimes(1);
  });

  it('calls onUploadFiles when files are selected', async () => {
    const uploadSpy = vi.fn();

    const { container } = render(
      <ProjectSidebar
        project={{
          ...sampleProject,
          contentFiles: [],
        }}
        expandedSections={new Set(['contentFiles'])}
        selectedItem={null}
        onSectionToggle={vi.fn()}
        onItemSelect={vi.fn()}
        onUploadFiles={uploadSpy}
      />
    );

    // The hidden input lives in the DOM even if it's hidden
    const fileInput = container.querySelector('input[type="file"]') as HTMLInputElement;
    expect(fileInput).not.toBeNull();

    // Simulate file upload via change event (userEvent.upload can fail on hidden inputs)
    const file = new File(['dummy content'], 'example.txt', { type: 'text/plain' });
    fireEvent.change(fileInput, { target: { files: [file] } });

    expect(uploadSpy).toHaveBeenCalledTimes(1);
  });

  it('invokes onSectionToggle for every section when its expand arrow is clicked', async () => {
    const toggleSpy = vi.fn();

    const projectWithData = {
      ...sampleProject,
      notebooks: [ { id: 'n1', title: 'Notebook 1'} ],
      contentFiles: [ { id: 'c1', fileName: 'file1.txt', path: '', contentType: 'text/plain', index: false, documentId: '', created: '' } ],
      userRoles: [ { userId: 'u1', userName: 'Bob', userEmail: 'bob@example.com', roleName: 'Contributor' } ],
    } as ProjectDetailsDto;

    render(
      <ProjectSidebar
        project={projectWithData}
        expandedSections={new Set()}
        selectedItem={null}
        onSectionToggle={toggleSpy}
        onItemSelect={vi.fn()}
      />
    );

    // There should be one expand arrow per section (3 total)
    const expandButtons = await screen.findAllByLabelText('Expand section');
    expect(expandButtons).toHaveLength(3);

    for (const btn of expandButtons) {
      await userEvent.click(btn);
    }

    // Expect every section type to have been toggled exactly once
    expect(toggleSpy).toHaveBeenCalledTimes(3);
    expect(toggleSpy).toHaveBeenCalledWith('notebooks');
    expect(toggleSpy).toHaveBeenCalledWith('contentFiles');
    expect(toggleSpy).toHaveBeenCalledWith('projectMembers');
  });

  it('calls onItemSelect for each section item when clicked', async () => {
    const selectSpy = vi.fn();

    const projectWithData = {
      ...sampleProject,
      notebooks: [ { id: 'n1', title: 'Notebook 1'} ],
      contentFiles: [ { id: 'c1', fileName: 'file1.txt', path: '', contentType: 'text/plain', index: false, documentId: '', created: '' } ],
      userRoles: [ { userId: 'u1', userName: 'Bob', userEmail: 'bob@example.com', roleName: 'Contributor' } ],
    } as ProjectDetailsDto;

    render(
      <ProjectSidebar
        project={projectWithData}
        expandedSections={new Set(['notebooks', 'contentFiles', 'projectMembers'])}
        selectedItem={null}
        onSectionToggle={vi.fn()}
        onItemSelect={selectSpy}
      />
    );

    // Click notebook
    await userEvent.click(screen.getByText('Notebook 1'));
    // Click content file
    await userEvent.click(screen.getByText('file1.txt'));
    // Click project member name
    await userEvent.click(screen.getByText('Bob'));

    expect(selectSpy).toHaveBeenCalledTimes(3);
    expect(selectSpy).toHaveBeenCalledWith('notebooks', 'n1');
    expect(selectSpy).toHaveBeenCalledWith('contentFiles', 'c1');
    expect(selectSpy).toHaveBeenCalledWith('projectMembers', 'u1');
  });
}); 