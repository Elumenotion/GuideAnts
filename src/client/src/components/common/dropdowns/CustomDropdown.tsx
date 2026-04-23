import React, { ReactNode } from 'react';
import { useDropdown, UseDropdownOptions } from '../../../hooks/useDropdown';

export interface CustomDropdownProps extends UseDropdownOptions {
  trigger: ReactNode;
  children: ReactNode;
  className?: string;
  disabled?: boolean;
  'data-tour-id'?: string;
}

export default function CustomDropdown({
  trigger,
  children,
  className = '',
  disabled = false,
  'data-tour-id': dataTourId,
  ...dropdownOptions
}: CustomDropdownProps) {
  const { isOpen, toggle, close, dropdownRef, triggerRef } = useDropdown(dropdownOptions);

  return (
    <div className={`relative inline-block ${className}`} ref={dropdownRef}>
      {/* Trigger Button */}
      <div>
        {React.cloneElement(trigger as React.ReactElement<any>, {
          ref: triggerRef,
          onClick: disabled ? undefined : toggle,
          'aria-expanded': isOpen,
          'aria-haspopup': 'listbox',
          disabled,
        })}
      </div>

      {/* Dropdown Panel */}
      {isOpen && !disabled && (
        <div 
          className="absolute top-full right-0 z-50 mt-1"
          data-tour-id={dataTourId}
          onClick={(e) => {
            // Close dropdown when clicking on option buttons
            if (e.target instanceof HTMLElement && 
                (e.target.closest('[role="option"]') || e.target.role === 'option')) {
              close();
            }
          }}
        >
          {children}
        </div>
      )}
    </div>
  );
} 