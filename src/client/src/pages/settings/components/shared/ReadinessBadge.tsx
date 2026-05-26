interface ReadinessBadgeProps {
  status: 'ready' | 'blocked' | 'not-configured' | string;
  label?: string;
}

const statusClass = (status: string): string => {
  if (status === 'ready') {
    return 'bg-emerald-100 text-emerald-800 border border-emerald-200';
  }

  if (status === 'blocked') {
    return 'bg-rose-100 text-rose-800 border border-rose-200';
  }

  if (status === 'not-configured') {
    return 'bg-slate-100 text-slate-700 border border-slate-200';
  }

  return 'bg-gray-100 text-gray-700 border border-gray-200';
};

export function ReadinessBadge({ status, label }: ReadinessBadgeProps) {
  const text = label ?? (status === 'not-configured' ? 'Not configured' : status);
  return (
    <span className={`inline-flex items-center rounded px-2 py-0.5 text-xs font-medium ${statusClass(status)}`}>
      {text}
    </span>
  );
}
