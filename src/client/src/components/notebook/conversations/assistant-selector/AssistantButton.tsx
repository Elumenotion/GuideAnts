import { forwardRef } from 'react';
import { resolveAgainstApiBase } from '../../../../config/apiConfig';
import { AssistantOption } from '../AssistantSelector';

export interface AssistantButtonProps {
  assistant?: AssistantOption;
  disabled?: boolean;
  onClick?: () => void;
  'aria-expanded'?: boolean;
  'aria-haspopup'?: 'listbox' | 'menu' | 'tree' | 'grid' | 'dialog' | boolean;
}

const AssistantButton = forwardRef<HTMLButtonElement, AssistantButtonProps>(({
  assistant,
  disabled = false,
  onClick,
  ...ariaProps
}, ref) => {
  // Resolve absolute avatar URL (same logic as before)
  let avatarSrc = assistant?.avatarUrl;
  if (avatarSrc && !avatarSrc.startsWith('http')) {
    avatarSrc = resolveAgainstApiBase(avatarSrc).toString();
  }

  return (
    <button
      ref={ref}
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={`
        flex items-center gap-2 md:gap-3 px-2 md:px-4 py-1 md:py-2
        bg-white border border-gray-300 rounded-md
        text-sm text-left
        hover:border-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500
        disabled:opacity-50 disabled:cursor-not-allowed
        transition-colors duration-150
      `}
      {...ariaProps}
    >
      {/* Avatar - smaller on mobile */}
      {avatarSrc && (
        <img
          src={avatarSrc}
          alt={`${assistant?.name} avatar`}
          className="h-6 w-6 md:h-9 md:w-9 rounded-full object-cover ring-1 ring-gray-200 flex-shrink-0"
        />
      )}

      {/* Assistant Name - smaller text on mobile */}
      <span className="flex-1 truncate text-sm md:text-base font-medium max-w-[100px] md:max-w-none">
        {assistant?.name || 'Select Guide'}
      </span>

      {/* Chevron */}
      <svg
        className="h-3 w-3 md:h-4 md:w-4 text-gray-400 flex-shrink-0"
        fill="none"
        stroke="currentColor"
        viewBox="0 0 24 24"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth={2}
          d="M9 5l7 7-7 7"
        />
      </svg>
    </button>
  );
});

AssistantButton.displayName = 'AssistantButton';

export default AssistantButton; 
