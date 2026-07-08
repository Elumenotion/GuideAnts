import { describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { FeaturesTab } from '../FeaturesTab';
import { InterfaceTab } from '../InterfaceTab';
import { LimitsTab } from '../LimitsTab';

describe('FeaturesTab', () => {
  it('toggles conversation starters and attachments', async () => {
    const user = userEvent.setup();
    const setShowConversationStarters = vi.fn();
    const setShowAttachments = vi.fn();

    render(
      <FeaturesTab
        showConversationStarters={false}
        setShowConversationStarters={setShowConversationStarters}
        showAttachments
        setShowAttachments={setShowAttachments}
      />,
    );

    const checkboxes = screen.getAllByRole('checkbox');
    await user.click(checkboxes[0]);
    await user.click(checkboxes[1]);

    expect(setShowConversationStarters).toHaveBeenCalledWith(true);
    expect(setShowAttachments).toHaveBeenCalledWith(false);
  });
});

describe('InterfaceTab', () => {
  it('updates display mode and interface toggles', async () => {
    const user = userEvent.setup();
    const setDisplayMode = vi.fn();
    const setCommandMode = vi.fn();
    const setShowTurnNavigation = vi.fn();
    const setCollapsible = vi.fn();
    const setShowSpeechToText = vi.fn();

    render(
      <InterfaceTab
        displayMode="full"
        setDisplayMode={setDisplayMode}
        commandMode={false}
        setCommandMode={setCommandMode}
        showTurnNavigation
        setShowTurnNavigation={setShowTurnNavigation}
        collapsible={false}
        setCollapsible={setCollapsible}
        showSpeechToText
        setShowSpeechToText={setShowSpeechToText}
      />,
    );

    await user.click(screen.getByRole('radio', { name: /Last Turn Only/i }));
    expect(setDisplayMode).toHaveBeenCalledWith('last-turn');

    const checkboxes = screen.getAllByRole('checkbox');
    await user.click(checkboxes[0]);
    await user.click(checkboxes[1]);
    await user.click(checkboxes[2]);
    await user.click(checkboxes[3]);

    expect(setCommandMode).toHaveBeenCalledWith(true);
    expect(setShowTurnNavigation).toHaveBeenCalledWith(false);
    expect(setCollapsible).toHaveBeenCalledWith(true);
    expect(setShowSpeechToText).toHaveBeenCalledWith(false);
  });
});

describe('LimitsTab', () => {
  it('parses numeric limits and clears empty values', async () => {
    const user = userEvent.setup();
    const setMaxUserMessageLength = vi.fn();
    const setRetentionPeriod = vi.fn();
    const setDailyChargeLimitUsd = vi.fn();

    render(
      <LimitsTab
        maxUserMessageLength={undefined}
        setMaxUserMessageLength={setMaxUserMessageLength}
        maxTurns={5}
        setMaxTurns={vi.fn()}
        retentionPeriod={0}
        setRetentionPeriod={setRetentionPeriod}
        dailyChargeLimitUsd={12}
        setDailyChargeLimitUsd={setDailyChargeLimitUsd}
        billingPeriodChargeLimitUsd={5}
        setBillingPeriodChargeLimitUsd={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText(/Max User Message Length/i), '500');
    expect(setMaxUserMessageLength).toHaveBeenCalled();

    await user.clear(screen.getByLabelText(/Retention Period/i));
    expect(setRetentionPeriod).toHaveBeenCalledWith(undefined);

    await user.clear(screen.getByLabelText(/Daily Cost Limit/i));
    expect(setDailyChargeLimitUsd).toHaveBeenCalledWith(undefined);
  });
});
