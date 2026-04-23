import { describe, it, expect, vi, beforeEach } from 'vitest';
import { useState } from 'react';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '../../../../test/test-utils';
import { api } from '../../../../services/api';
import { ToolsSelector } from '../ToolsSelector';

vi.mock('../../../../services/api');

describe('ToolsSelector', () => {
  const tools = [
    {
      id: 'b0000000-0000-0000-0000-00000000000a',
      toolType: 'search_notebook',
      displayName: 'Search Notebook',
      description: 'Search inside notebook',
      category: 'Search',
      isActive: true,
      displayOrder: 2,
    },
    {
      id: 'b0000000-0000-0000-0000-000000000001',
      toolType: 'ReadWeb',
      displayName: 'Read Web',
      description: 'Read web page',
      category: 'Search',
      isActive: true,
      displayOrder: 1,
    },
    {
      id: 'b0000000-0000-0000-0000-00000000000b',
      toolType: 'search_project',
      displayName: 'Search Project',
      description: 'Search inside project',
      category: 'Search',
      isActive: true,
      displayOrder: 3,
    },
    {
      id: 'b0000000-0000-0000-0000-00000000000d',
      toolType: 'WebSearch',
      displayName: 'Web Search',
      description: 'Search the web',
      category: 'Search',
      isActive: true,
      displayOrder: 0,
    },
  ];

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.guides.catalogs.tools).mockResolvedValue([...tools].reverse());
  });

  it('shows Web Search first in Search category', async () => {
    render(
      <ToolsSelector
        selectedToolIds={[]}
        contextOptions={[]}
        onSelectedToolIdsChange={() => {}}
      />
    );

    await screen.findByText('Search');

    await waitFor(() => {
      const searchHeading = screen.getByText('Search');
      const searchSection = searchHeading.closest('.space-y-2') as HTMLElement;
      const names = Array.from(
        searchSection.querySelectorAll('div.text-sm.font-medium.text-gray-900')
      ).map((el) => el.textContent?.trim());

      expect(names.slice(0, 4)).toEqual([
        'Web Search',
        'Read Web',
        'Search Notebook',
        'Search Project',
      ]);
    });
  });

  it('toggles Web Search id in selected tools', async () => {
    const user = userEvent.setup();

    function Harness() {
      const [selected, setSelected] = useState<string[]>([]);
      return (
        <>
          <ToolsSelector
            selectedToolIds={selected}
            contextOptions={[]}
            onSelectedToolIdsChange={setSelected}
          />
          <div data-testid="selected-tools">{selected.join(',')}</div>
        </>
      );
    }

    render(<Harness />);

    const checkbox = await screen.findByRole('checkbox', { name: /Web Search/i });
    await user.click(checkbox);

    expect(screen.getByTestId('selected-tools').textContent).toContain('b0000000-0000-0000-0000-00000000000d');
  });
});
