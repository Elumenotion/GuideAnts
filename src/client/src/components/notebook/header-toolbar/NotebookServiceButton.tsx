import type { ReactElement } from 'react';
import {
  statusDotClass,
  toolbarServiceButtonClass,
  toolbarServiceStatusDotBorderClass,
  withToolbarServiceIcon,
  type ToolbarServiceColorKey,
} from './toolbarFormatters';

interface NotebookServiceButtonProps {
  serviceId: ToolbarServiceColorKey;
  label: string;
  tooltip?: string;
  icon: ReactElement;
  status: string;
  expanded: boolean;
  onClick: () => void;
}

export function NotebookServiceButton({
  serviceId,
  label,
  tooltip,
  icon,
  status,
  expanded,
  onClick,
}: NotebookServiceButtonProps) {
  return (
    <button
      type="button"
      className={toolbarServiceButtonClass(serviceId, { expanded, minSize: 'md' })}
      aria-expanded={expanded}
      aria-haspopup="dialog"
      aria-label={label}
      onClick={onClick}
      title={tooltip ?? label}
    >
      <span
        className={`absolute top-0.5 right-0.5 h-1.5 w-1.5 rounded-full border ${toolbarServiceStatusDotBorderClass(
          serviceId
        )} ${statusDotClass(status)}`}
        aria-hidden="true"
      />
      {withToolbarServiceIcon(serviceId, icon)}
    </button>
  );
}
