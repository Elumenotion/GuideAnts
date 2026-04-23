import { ReactNode } from 'react';

export interface DropdownPanelProps {
  children: ReactNode;
  className?: string;
  width?: string;
  maxHeight?: string;
}

export default function DropdownPanel({
  children,
  className = '',
  width = 'w-72',
  maxHeight = 'max-h-96',
}: DropdownPanelProps) {
  return (
    <div
      className={`
        ${width} ${maxHeight}
        bg-white rounded-md border border-gray-200 shadow-lg
        overflow-hidden
        ${className}
      `}
      role="listbox"
    >
      {children}
    </div>
  );
} 