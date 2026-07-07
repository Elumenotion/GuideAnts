import { describe, expect, it } from 'vitest';
import {
  DAY_LABELS,
  buildScheduleSummary,
  defaultFriendlySchedule,
} from '../scheduledJob';

describe('scheduledJob helpers', () => {
  it('provides day labels and a daily default schedule', () => {
    expect(DAY_LABELS).toHaveLength(7);
    expect(defaultFriendlySchedule()).toEqual({
      frequency: 'Daily',
      timeOfDay: '09:00',
      daysOfWeek: [1, 2, 3, 4, 5],
      dayOfMonth: 1,
      hourlyIntervalMinutes: 60,
      customCronExpression: '',
    });
  });

  it('summarizes hourly schedules', () => {
    expect(
      buildScheduleSummary(
        { frequency: 'Hourly', hourlyIntervalMinutes: 30 },
        'UTC',
      ),
    ).toBe('Every 30 minutes (UTC)');

    expect(
      buildScheduleSummary(
        { frequency: 'Hourly', hourlyIntervalMinutes: 60 },
        'America/New_York',
      ),
    ).toBe('Every hour (America/New_York)');
  });

  it('summarizes daily, weekly, monthly, and custom schedules', () => {
    expect(
      buildScheduleSummary({ frequency: 'Daily', timeOfDay: '14:30' }, 'UTC'),
    ).toBe('Daily at 14:30 (UTC)');

    expect(
      buildScheduleSummary({ frequency: 'Weekly', timeOfDay: '08:00' }, 'UTC'),
    ).toBe('Weekly at 08:00 (UTC)');

    expect(
      buildScheduleSummary({ frequency: 'Monthly', dayOfMonth: 15, timeOfDay: '10:15' }, 'UTC'),
    ).toBe('Monthly on day 15 at 10:15 (UTC)');

    expect(
      buildScheduleSummary(
        { frequency: 'Custom', customCronExpression: '0 9 * * 1-5' },
        'UTC',
      ),
    ).toBe('Custom: 0 9 * * 1-5 (UTC)');
  });

  it('falls back to timezone when frequency is unknown', () => {
    expect(
      buildScheduleSummary({ frequency: 'Nightly' as never }, 'Pacific/Auckland'),
    ).toBe('Pacific/Auckland');
  });
});
