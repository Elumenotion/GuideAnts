import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '../../../../test/test-utils';
import { ToolsTab } from '../ToolsTab';

vi.mock('../ToolsSelector', () => ({
  ToolsSelector: () => <div>Global Tools Mock</div>,
}));

vi.mock('../OpenApiSchemas', () => ({
  OpenApiSchemas: () => <div>Tool Sources Panel</div>,
}));

describe('ToolsTab', () => {
  it('shows Tool Sources subtab label', () => {
    render(
      <ToolsTab
        selectedToolIds={[]}
        customTools={[]}
        contextOptions={[]}
        environmentVariables={[]}
        onSelectedToolIdsChange={() => {}}
        onCustomToolsChange={() => {}}
        onEnvironmentVariablesChange={() => {}}
      />
    );

    expect(screen.getByRole('tab', { name: 'Tool Sources' })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Web Connectors' })).not.toBeInTheDocument();
  });

  it('switches to tool sources panel', async () => {
    const user = userEvent.setup();
    render(
      <ToolsTab
        selectedToolIds={[]}
        customTools={[]}
        contextOptions={[]}
        environmentVariables={[]}
        onSelectedToolIdsChange={() => {}}
        onCustomToolsChange={() => {}}
        onEnvironmentVariablesChange={() => {}}
      />
    );

    await user.click(screen.getByRole('tab', { name: 'Tool Sources' }));
    expect(screen.getByText('Tool Sources Panel')).toBeInTheDocument();
  });
});
