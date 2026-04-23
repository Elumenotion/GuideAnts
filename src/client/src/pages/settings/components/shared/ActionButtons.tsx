import type { ReactNode } from 'react';

export type ButtonTone = 'primary' | 'neutral' | 'accent' | 'info' | 'success' | 'danger';

const BUTTON_TEXT_BASE_CLASS =
  'inline-flex h-8 items-center justify-center gap-1.5 whitespace-nowrap rounded-md border px-3 text-xs font-medium tracking-[0.01em] shadow-sm transition-all duration-150 hover:-translate-y-px active:translate-y-0 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1 disabled:cursor-not-allowed disabled:opacity-45 disabled:hover:translate-y-0';

const BUTTON_ICON_BASE_CLASS =
  'inline-flex h-8 w-8 items-center justify-center rounded-md border shadow-sm transition-all duration-150 hover:-translate-y-px active:translate-y-0 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1 disabled:cursor-not-allowed disabled:opacity-45 disabled:hover:translate-y-0';

const BUTTON_TONE_CLASS: Record<ButtonTone, string> = {
  primary: 'border-blue-600 bg-blue-600 text-white hover:border-blue-700 hover:bg-blue-700',
  neutral: 'border-slate-300 bg-white text-slate-700 hover:border-slate-400 hover:bg-slate-50',
  accent: 'border-indigo-200 bg-indigo-50 text-indigo-800 hover:border-indigo-300 hover:bg-indigo-100',
  info: 'border-sky-200 bg-sky-50 text-sky-800 hover:border-sky-300 hover:bg-sky-100',
  success: 'border-emerald-200 bg-emerald-50 text-emerald-800 hover:border-emerald-300 hover:bg-emerald-100',
  danger: 'border-red-200 bg-red-50 text-red-700 hover:border-red-300 hover:bg-red-100',
};

export function textButtonClassName(tone: ButtonTone): string {
  return `${BUTTON_TEXT_BASE_CLASS} ${BUTTON_TONE_CLASS[tone]}`;
}

export function iconButtonClassName(tone: ButtonTone): string {
  return `${BUTTON_ICON_BASE_CLASS} ${BUTTON_TONE_CLASS[tone]}`;
}

export function IconActionButton({
  label,
  tone,
  icon,
  title,
  disabled,
  onClick,
}: {
  label: string;
  tone: ButtonTone;
  icon: ReactNode;
  title?: string;
  disabled?: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      className={iconButtonClassName(tone)}
      title={title ?? label}
    >
      <span className="text-[13px] leading-none" aria-hidden="true">
        {icon}
      </span>
      <span className="sr-only">{label}</span>
    </button>
  );
}

export function TextActionButton({
  tone,
  icon,
  children,
  title,
  disabled,
  onClick,
}: {
  tone: ButtonTone;
  icon?: ReactNode;
  children: ReactNode;
  title?: string;
  disabled?: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={textButtonClassName(tone)}
      title={title}
    >
      {icon ? (
        <span className="text-[12px] leading-none" aria-hidden="true">
          {icon}
        </span>
      ) : null}
      <span>{children}</span>
    </button>
  );
}
