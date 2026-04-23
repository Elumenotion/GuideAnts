import React from 'react';
import { render, screen } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import DraftUserCell from '../DraftUserCell';

// Polyfill getBoundingClientRect for Text/Node
if (!(Node.prototype as any).getBoundingClientRect) {
  (Node.prototype as any).getBoundingClientRect = () => ({ x:0,y:0,width:0,height:0,top:0,right:0,bottom:0,left:0 });
}

const SAMPLE = `Line one.

Line two after blank line.


Line three after double blank.`;

describe('Line break fidelity through toggle', () => {
  it('preserves consecutive blank lines after full-screen round-trip', async () => {
    const onChange = vi.fn();
    const onSend = vi.fn();

    const user = userEvent.setup();
    render(<DraftUserCell value={SAMPLE} onChange={onChange} onSend={onSend} />);

    // open full screen
    await user.click(screen.getByLabelText(/full screen/i));

    // Save (send) -> uses same markdown
    const sendBtn = await screen.findByRole('button', { name: /send/i });
    await user.click(sendBtn);

    // verify last onChange called with identical markdown (incl linefeeds)
    expect(onChange).toHaveBeenLastCalledWith(SAMPLE);
  });
}); 