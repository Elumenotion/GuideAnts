import type { FriendlyScheduleDto, ScheduleFrequency } from '../../../types/scheduledJob';
import { DAY_LABELS } from '../../../types/scheduledJob';

interface ScheduleBuilderProps {
  schedule: FriendlyScheduleDto;
  timeZoneId: string;
  onScheduleChange: (schedule: FriendlyScheduleDto) => void;
  onTimeZoneChange: (timeZoneId: string) => void;
  disabled?: boolean;
  previewText?: string;
}

const TIMEZONE_OPTIONS = typeof Intl !== 'undefined' && 'supportedValuesOf' in Intl
  ? Intl.supportedValuesOf('timeZone')
  : ['UTC'];

export function ScheduleBuilder({
  schedule,
  timeZoneId,
  onScheduleChange,
  onTimeZoneChange,
  disabled = false,
  previewText,
}: ScheduleBuilderProps) {
  const update = (partial: Partial<FriendlyScheduleDto>) => {
    onScheduleChange({ ...schedule, ...partial });
  };

  return (
    <div className="space-y-4">
      <div>
        <label htmlFor="schedule-frequency" className="block text-sm font-medium text-gray-700 mb-1">
          Frequency
        </label>
        <select
          id="schedule-frequency"
          value={schedule.frequency}
          onChange={(e) => update({ frequency: e.target.value as ScheduleFrequency })}
          disabled={disabled}
          className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
        >
          <option value="Hourly">Hourly</option>
          <option value="Daily">Daily</option>
          <option value="Weekly">Weekly</option>
          <option value="Monthly">Monthly</option>
          <option value="Custom">Custom (cron)</option>
        </select>
      </div>

      {schedule.frequency === 'Hourly' && (
        <div>
          <label htmlFor="hourly-interval" className="block text-sm font-medium text-gray-700 mb-1">
            Run every (minutes)
          </label>
          <input
            id="hourly-interval"
            type="number"
            min={1}
            max={60}
            value={schedule.hourlyIntervalMinutes ?? 60}
            onChange={(e) => update({ hourlyIntervalMinutes: Number(e.target.value) })}
            disabled={disabled}
            className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
          />
          <p className="mt-1 text-xs text-gray-500">Use 60 for once per hour at minute 0.</p>
        </div>
      )}

      {(schedule.frequency === 'Daily' || schedule.frequency === 'Weekly' || schedule.frequency === 'Monthly') && (
        <div>
          <label htmlFor="schedule-time" className="block text-sm font-medium text-gray-700 mb-1">
            Time
          </label>
          <input
            id="schedule-time"
            type="time"
            value={schedule.timeOfDay ?? '09:00'}
            onChange={(e) => update({ timeOfDay: e.target.value })}
            disabled={disabled}
            className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
          />
        </div>
      )}

      {schedule.frequency === 'Weekly' && (
        <fieldset>
          <legend className="block text-sm font-medium text-gray-700 mb-2">Days of week</legend>
          <div className="flex flex-wrap gap-2">
            {DAY_LABELS.map((label, index) => {
              const selected = schedule.daysOfWeek?.includes(index) ?? false;
              return (
                <label key={label} className="inline-flex items-center gap-1 text-sm">
                  <input
                    type="checkbox"
                    checked={selected}
                    disabled={disabled}
                    onChange={(e) => {
                      const current = new Set(schedule.daysOfWeek ?? []);
                      if (e.target.checked) {
                        current.add(index);
                      } else {
                        current.delete(index);
                      }
                      update({ daysOfWeek: Array.from(current).sort((a, b) => a - b) });
                    }}
                    className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                  />
                  {label}
                </label>
              );
            })}
          </div>
        </fieldset>
      )}

      {schedule.frequency === 'Monthly' && (
        <div>
          <label htmlFor="day-of-month" className="block text-sm font-medium text-gray-700 mb-1">
            Day of month
          </label>
          <select
            id="day-of-month"
            value={schedule.dayOfMonth ?? 1}
            onChange={(e) => update({ dayOfMonth: Number(e.target.value) })}
            disabled={disabled}
            className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
          >
            {Array.from({ length: 31 }, (_, i) => i + 1).map((day) => (
              <option key={day} value={day}>{day}</option>
            ))}
          </select>
        </div>
      )}

      {schedule.frequency === 'Custom' && (
        <div>
          <label htmlFor="custom-cron" className="block text-sm font-medium text-gray-700 mb-1">
            Cron expression
          </label>
          <input
            id="custom-cron"
            type="text"
            value={schedule.customCronExpression ?? ''}
            onChange={(e) => update({ customCronExpression: e.target.value })}
            disabled={disabled}
            placeholder="0 9 * * *"
            className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm font-mono focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
            aria-describedby="custom-cron-help"
          />
          <p id="custom-cron-help" className="mt-1 text-xs text-gray-500">
            Standard 5-field cron: minute hour day month day-of-week (0=Sunday).
          </p>
        </div>
      )}

      <div>
        <label htmlFor="schedule-timezone" className="block text-sm font-medium text-gray-700 mb-1">
          Timezone
        </label>
        <select
          id="schedule-timezone"
          value={timeZoneId}
          onChange={(e) => onTimeZoneChange(e.target.value)}
          disabled={disabled}
          className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
        >
          {TIMEZONE_OPTIONS.map((tz) => (
            <option key={tz} value={tz}>{tz}</option>
          ))}
        </select>
      </div>

      {previewText && (
        <p className="text-sm text-gray-600 bg-gray-50 rounded-md px-3 py-2" aria-live="polite">
          {previewText}
        </p>
      )}
    </div>
  );
}
