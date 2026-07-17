import { useCallback, useEffect, useMemo, useState } from 'react';
import { FaSave, FaSyncAlt } from 'react-icons/fa';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { useToast } from '../../../components/common/Toast';
import { api } from '../../../services/api';
import { SettingsSectionDto } from '../../../types/settings';
import { TextActionButton } from './shared/ActionButtons';
import { IntInput } from './inputs/IntInput';

const SECTION_NAME = 'ScriptExecution';
const MIN_TIMEOUT_SECONDS = 1;
const MAX_TIMEOUT_SECONDS = 7200;
const DEFAULT_TIMEOUT_SECONDS = 600;

function readTimeoutSeconds(section: SettingsSectionDto): number {
  const raw = section.payload.TimeoutSeconds;
  if (typeof raw === 'number' && Number.isFinite(raw) && raw > 0) {
    return raw;
  }
  if (typeof raw === 'string') {
    const parsed = Number.parseInt(raw, 10);
    if (Number.isFinite(parsed) && parsed > 0) {
      return parsed;
    }
  }
  return DEFAULT_TIMEOUT_SECONDS;
}

function formatDuration(seconds: number): string {
  if (seconds % 60 === 0) {
    return `${seconds / 60} minutes`;
  }
  return `${seconds} seconds`;
}

export function SandboxTab() {
  const { showToast } = useToast();
  const [section, setSection] = useState<SettingsSectionDto | null>(null);
  const [initialTimeoutSeconds, setInitialTimeoutSeconds] = useState<number | null>(null);
  const [timeoutSeconds, setTimeoutSeconds] = useState<number | ''>(DEFAULT_TIMEOUT_SECONDS);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const loadSandboxSettings = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const nextSection = await api.settings.getSection(SECTION_NAME);
      const nextTimeout = readTimeoutSeconds(nextSection);
      setSection(nextSection);
      setInitialTimeoutSeconds(nextTimeout);
      setTimeoutSeconds(nextTimeout);
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : 'Failed to load sandbox settings.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadSandboxSettings();
  }, [loadSandboxSettings]);

  const validationError = useMemo(() => {
    if (timeoutSeconds === '') {
      return 'Timeout is required.';
    }
    if (!Number.isInteger(timeoutSeconds) || timeoutSeconds < MIN_TIMEOUT_SECONDS) {
      return `Timeout must be at least ${MIN_TIMEOUT_SECONDS} second.`;
    }
    if (timeoutSeconds > MAX_TIMEOUT_SECONDS) {
      return `Timeout must be ${MAX_TIMEOUT_SECONDS} seconds (${formatDuration(MAX_TIMEOUT_SECONDS)}) or less.`;
    }
    return null;
  }, [timeoutSeconds]);

  const isDirty = useMemo(() => {
    if (initialTimeoutSeconds == null || timeoutSeconds === '') {
      return false;
    }
    return timeoutSeconds !== initialTimeoutSeconds;
  }, [initialTimeoutSeconds, timeoutSeconds]);

  const saveSandboxSettings = useCallback(async () => {
    if (!section || timeoutSeconds === '' || validationError) {
      return;
    }

    setSaving(true);
    try {
      const saved = await api.settings.updateSection(SECTION_NAME, {
        rowVersion: section.rowVersion,
        payload: {
          TimeoutSeconds: timeoutSeconds,
        },
      });
      const nextTimeout = readTimeoutSeconds(saved);
      setSection(saved);
      setInitialTimeoutSeconds(nextTimeout);
      setTimeoutSeconds(nextTimeout);
      showToast({
        type: 'success',
        title: 'Sandbox settings saved',
        message: 'New script execution timeouts apply to the next sandbox tool run.',
      });
    } catch (error) {
      const status = (error as { status?: number })?.status;
      showToast({
        type: 'error',
        title: status === 409 ? 'Sandbox settings changed elsewhere' : 'Failed to save sandbox settings',
        message: status === 409
          ? 'Refresh the Sandbox tab and try again.'
          : error instanceof Error
            ? error.message
            : 'The sandbox update request failed.',
      });
    } finally {
      setSaving(false);
    }
  }, [section, showToast, timeoutSeconds, validationError]);

  if (loading) {
    return (
      <section className="rounded-lg border border-gray-200 bg-white p-8">
        <LoadingSpinner message="Loading sandbox settings..." />
      </section>
    );
  }

  if (loadError || !section) {
    return (
      <section className="rounded-lg border border-red-200 bg-red-50 p-6">
        <h2 className="text-base font-semibold text-red-900">Sandbox settings unavailable</h2>
        <p className="mt-1 text-sm text-red-700">{loadError ?? 'Sandbox settings could not be loaded.'}</p>
        <div className="mt-4">
          <TextActionButton tone="neutral" icon={<FaSyncAlt />} onClick={() => void loadSandboxSettings()}>
            Retry
          </TextActionButton>
        </div>
      </section>
    );
  }

  return (
    <section className="space-y-5">
      <div className="rounded-lg border border-gray-200 bg-white">
        <div className="flex flex-col gap-3 border-b border-gray-200 px-6 py-4 md:flex-row md:items-center md:justify-between">
          <div>
            <h2 className="text-base font-semibold text-gray-900">Sandbox</h2>
            <p className="mt-1 text-sm text-gray-600">
              Configure how long Python, Bash, and PowerShell tool runs may execute in the guideants-ai sandbox.
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <TextActionButton tone="neutral" icon={<FaSyncAlt />} onClick={() => void loadSandboxSettings()} disabled={saving}>
              Refresh
            </TextActionButton>
            <TextActionButton
              tone="primary"
              icon={<FaSave />}
              onClick={() => void saveSandboxSettings()}
              disabled={!isDirty || saving || validationError != null}
            >
              {saving ? 'Saving...' : 'Save'}
            </TextActionButton>
          </div>
        </div>
        <div className="grid gap-4 px-6 py-4 md:grid-cols-3">
          <div>
            <div className="text-xs font-medium uppercase tracking-wide text-gray-500">Default</div>
            <div className="mt-1 text-sm text-gray-900">{formatDuration(DEFAULT_TIMEOUT_SECONDS)}</div>
          </div>
          <div>
            <div className="text-xs font-medium uppercase tracking-wide text-gray-500">Apply behavior</div>
            <div className="mt-1 text-sm text-gray-900">Saved changes apply to the next sandbox execution request</div>
          </div>
          <div>
            <div className="text-xs font-medium uppercase tracking-wide text-gray-500">State</div>
            <div className={`mt-1 text-sm font-medium ${isDirty ? 'text-amber-700' : 'text-emerald-700'}`}>
              {isDirty ? 'Unsaved changes' : 'Saved'}
            </div>
          </div>
        </div>
      </div>

      <div className="rounded-lg border border-gray-200 bg-white">
        <div className="border-b border-gray-200 px-6 py-4">
          <h3 className="text-sm font-semibold text-gray-900">Execution Limits</h3>
        </div>
        <div className="space-y-4 px-6 py-4">
          <div className="max-w-md">
            <label htmlFor="sandbox-timeout-seconds" className="block text-sm font-medium text-gray-900">
              Script execution timeout (seconds)
            </label>
            <p className="mt-1 text-sm text-gray-600">
              Maximum time allowed for a single sandbox script before the API and script agent stop the run.
            </p>
            <div className="mt-2">
              <IntInput
                id="sandbox-timeout-seconds"
                value={timeoutSeconds}
                min={MIN_TIMEOUT_SECONDS}
                max={MAX_TIMEOUT_SECONDS}
                onChange={setTimeoutSeconds}
              />
            </div>
            {timeoutSeconds !== '' && !validationError && (
              <p className="mt-2 text-sm text-gray-500">Current limit: {formatDuration(timeoutSeconds)}</p>
            )}
            {validationError && (
              <p className="mt-2 text-sm text-red-600" role="alert">
                {validationError}
              </p>
            )}
          </div>
          <p className="text-sm text-gray-600">
            Docker deployments can also set <code className="rounded bg-gray-100 px-1 py-0.5 text-xs">GA_SCRIPT_EXECUTION_TIMEOUT_SECONDS</code>{' '}
            for container startup defaults. Settings saved here take effect immediately for new tool runs without restarting containers.
          </p>
        </div>
      </div>
    </section>
  );
}
