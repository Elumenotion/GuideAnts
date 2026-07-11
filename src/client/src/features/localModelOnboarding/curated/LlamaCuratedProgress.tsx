import { FaCheck, FaSpinner } from 'react-icons/fa';
import type { AddModelErrorDto } from '../../../types/settings';
import { localModelOnboardingProgressStep } from '../status';

const PROGRESS_STEPS = [
  { id: 'queued', label: 'Queued' },
  { id: 'resolvingFiles', label: 'Resolving files' },
  { id: 'downloading', label: 'Downloading' },
  { id: 'registeringAlias', label: 'Registering alias' },
  { id: 'completed', label: 'Completed' },
] as const;

interface LlamaCuratedProgressProps {
  status: string;
  progress: number | null;
  logLine?: string | null;
  error?: AddModelErrorDto | null;
}

export function LlamaCuratedProgress({
  status,
  progress,
  logLine,
  error,
}: LlamaCuratedProgressProps) {
  const currentStep = localModelOnboardingProgressStep(status);
  const currentIndex = PROGRESS_STEPS.findIndex((step) => step.id === currentStep);
  const pct = progress != null ? Math.round(Math.min(1, Math.max(0, progress)) * 100) : null;

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-1 text-sm">
        {PROGRESS_STEPS.map((step, index) => {
          const done = index < currentIndex;
          const active = index === currentIndex;
          const future = index > currentIndex;
          return (
            <span key={step.id} className="flex items-center gap-1">
              {index > 0 ? <span className={`mx-0.5 text-xs ${future ? 'text-gray-300' : 'text-gray-400'}`}>&rsaquo;</span> : null}
              {active && status !== 'completed' ? <FaSpinner className="animate-spin text-blue-600" /> : null}
              {done || (active && status === 'completed') ? <FaCheck className="text-emerald-500" /> : null}
              <span className={active ? 'font-medium text-blue-700' : done ? 'text-gray-700' : 'text-gray-400'}>
                {step.label}
              </span>
            </span>
          );
        })}
      </div>

      {pct != null && currentStep === 'downloading' ? (
        <div>
          <div className="mb-1 text-xs text-gray-500">{pct}%</div>
          <div className="h-2 w-full overflow-hidden rounded-full bg-gray-200">
            <div className="h-full rounded-full bg-blue-500 transition-all" style={{ width: `${pct}%` }} />
          </div>
        </div>
      ) : null}

      {logLine ? <div className="font-mono text-xs text-gray-500">{logLine}</div> : null}

      {error ? (
        <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
          <div className="font-medium">{error.code}</div>
          <div>{error.message}</div>
          {error.remediation ? <div className="mt-1 text-xs">{error.remediation}</div> : null}
        </div>
      ) : null}
    </div>
  );
}
