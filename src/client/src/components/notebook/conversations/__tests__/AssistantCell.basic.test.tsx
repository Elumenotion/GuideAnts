import React from 'react';
import { render } from '../../../../test/test-utils';
import AssistantCell from '../AssistantCell';
import { describe, it, expect, vi } from 'vitest';

// Make portal render inline for jsdom
vi.mock('react-dom', async () => {
  const actual = await vi.importActual<typeof import('react-dom')>('react-dom');
  return { ...actual, createPortal: (node: any) => node };
});

describe('AssistantCell', () => {
  it('shows Thinking… indicator while streaming and content empty', () => {
    const { getByText } = render(
      <AssistantCell
        content=""
        isLast={false}
        isStreaming={true}
      />
    );

    expect(getByText(/thinking/i)).toBeInTheDocument();
  });

  it('displays Edited badge when isEdited=true', () => {
    const { getByText } = render(
      <AssistantCell
        content="new content"
        originalContent="old content"
        isEdited={true}
        isLast={false}
      />
    );

    expect(getByText(/edited/i)).toBeInTheDocument();
  });
}); 