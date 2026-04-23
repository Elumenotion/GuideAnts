import { textButtonClassName } from '../../../pages/settings/components/shared/ActionButtons';
import { statusDotClass } from './toolbarFormatters';

interface NotebookServiceButtonProps {
  label: string;
  status: string;
  expanded: boolean;
  onClick: () => void;
}

export function NotebookServiceButton({
  label,
  status,
  expanded,
  onClick,
}: NotebookServiceButtonProps) {
  return (
    <button
      type="button"
      className={`${textButtonClassName('neutral')} text-xs max-w-[8.5rem]`}
      aria-expanded={expanded}
      aria-haspopup="dialog"
      onClick={onClick}
      title={label}
    >
      <span
        className={`inline-block h-2 w-2 min-w-[0.5rem] rounded-full ${statusDotClass(status)}`}
        aria-hidden="true"
      />
      <span className="truncate">{label}</span>
    </button>
  );
}
