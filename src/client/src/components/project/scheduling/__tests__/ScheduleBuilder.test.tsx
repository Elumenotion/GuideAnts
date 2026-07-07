import { describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ScheduleBuilder } from '../ScheduleBuilder';
import { defaultFriendlySchedule } from '../../../../types/scheduledJob';

describe('ScheduleBuilder', () => {
  it('updates frequency-specific fields and timezone', async () => {
    const user = userEvent.setup();
    const onScheduleChange = vi.fn();
    const onTimeZoneChange = vi.fn();

    const { rerender } = render(
      <ScheduleBuilder
        schedule={defaultFriendlySchedule()}
        timeZoneId="UTC"
        onScheduleChange={onScheduleChange}
        onTimeZoneChange={onTimeZoneChange}
        previewText="Daily at 09:00 (UTC)"
      />,
    );

    expect(screen.getByText('Daily at 09:00 (UTC)')).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Frequency'), 'Custom');
    expect(onScheduleChange).toHaveBeenCalledWith(
      expect.objectContaining({ frequency: 'Custom' }),
    );

    rerender(
      <ScheduleBuilder
        schedule={{
          ...defaultFriendlySchedule(),
          frequency: 'Custom',
          customCronExpression: '',
        }}
        timeZoneId="UTC"
        onScheduleChange={onScheduleChange}
        onTimeZoneChange={onTimeZoneChange}
      />,
    );

    await user.type(screen.getByLabelText('Cron expression'), '0 8 * * *');
    expect(onScheduleChange).toHaveBeenCalledWith(
      expect.objectContaining({ customCronExpression: expect.any(String) }),
    );
  });

  it('toggles weekly days and edits hourly interval', async () => {
    const user = userEvent.setup();
    const onScheduleChange = vi.fn();

    const { rerender } = render(
      <ScheduleBuilder
        schedule={{
          ...defaultFriendlySchedule(),
          frequency: 'Weekly',
          daysOfWeek: [1],
        }}
        timeZoneId="UTC"
        onScheduleChange={onScheduleChange}
        onTimeZoneChange={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('checkbox', { name: 'Wed' }));
    expect(onScheduleChange).toHaveBeenCalledWith(
      expect.objectContaining({ daysOfWeek: expect.arrayContaining([1, 3]) }),
    );

    rerender(
      <ScheduleBuilder
        schedule={{
          ...defaultFriendlySchedule(),
          frequency: 'Hourly',
          hourlyIntervalMinutes: 60,
        }}
        timeZoneId="UTC"
        onScheduleChange={onScheduleChange}
        onTimeZoneChange={vi.fn()}
      />,
    );

    await user.clear(screen.getByLabelText('Run every (minutes)'));
    await user.type(screen.getByLabelText('Run every (minutes)'), '15');
    expect(onScheduleChange).toHaveBeenCalledWith(
      expect.objectContaining({ hourlyIntervalMinutes: expect.any(Number) }),
    );
  });
});
