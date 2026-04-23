import React from 'react';
import '@testing-library/jest-dom';
import { describe, it, expect } from 'vitest';
import { render, screen } from '../../../test/test-utils';
import { TwoColumnLayout } from '../TwoColumnLayout';

describe('TwoColumnLayout', () => {
  it('respects provided left column width', () => {
    render(
      <TwoColumnLayout
        leftColumn={<div data-testid="left">Left</div>}
        rightColumn={<div data-testid="right">Right</div>}
        leftColumnWidth={300}
      />
    );

    const leftWrapper = screen.getByTestId('left').parentElement as HTMLElement;
    expect(leftWrapper).toHaveStyle({ width: '300px' });
  });

  it('renders collapsed left column when isLeftCollapsed=true', () => {
    render(
      <TwoColumnLayout
        leftColumn={<div data-testid="left">Left</div>}
        rightColumn={<div>Right</div>}
        isLeftCollapsed
      />
    );

    const leftWrapper = screen.getByTestId('left').parentElement as HTMLElement;
    expect(leftWrapper).toHaveStyle({ width: '40px' });
  });
}); 