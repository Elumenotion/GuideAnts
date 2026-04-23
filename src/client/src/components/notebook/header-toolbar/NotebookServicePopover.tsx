import type { ReactNode } from 'react';
import DropdownPanel from '../../common/dropdowns/DropdownPanel';

interface NotebookServicePopoverProps {
  open: boolean;
  children: ReactNode;
}

export function NotebookServicePopover({ open, children }: NotebookServicePopoverProps) {
  if (!open) return null;

  return (
    <div
      className="absolute top-full right-0 z-50 mt-1"
      role="dialog"
      aria-modal="false"
      onMouseDown={(event) => event.stopPropagation()}
    >
      <DropdownPanel width="w-80" maxHeight="max-h-[70vh]">
        <div className="p-3">{children}</div>
      </DropdownPanel>
    </div>
  );
}
