import { ReadinessBadge } from './ReadinessBadge';

interface OperationalDependencyRowProps {
  keyName: string;
  displayName: string;
  hasValue: boolean;
  currentValue?: string | null;
  changeHint?: string;
}

export function OperationalDependencyRow({
  keyName,
  displayName,
  hasValue,
  currentValue,
  changeHint,
}: OperationalDependencyRowProps) {
  return (
    <div className="rounded border border-gray-200 p-3">
      <div className="flex items-start justify-between gap-3">
        <div className="space-y-1">
          <div className="text-sm font-medium text-gray-900">{displayName}</div>
          <div className="font-mono text-xs text-gray-600">{keyName}</div>
        </div>
        <ReadinessBadge status={hasValue ? 'ready' : 'blocked'} label={hasValue ? 'Configured' : 'Missing'} />
      </div>
      {currentValue ? <div className="mt-2 break-all text-xs text-gray-700">{currentValue}</div> : null}
      {changeHint ? <div className="mt-2 text-xs text-gray-500">{changeHint}</div> : null}
    </div>
  );
}
