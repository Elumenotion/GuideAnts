import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '../../../../test/test-utils';
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
}); 