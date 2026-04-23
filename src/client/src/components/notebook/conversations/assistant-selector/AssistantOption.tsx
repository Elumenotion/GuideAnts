// No React import needed for this component
import { AssistantOption as AssistantOptionType } from '../AssistantSelector';
import { resolveAgainstApiBase } from '../../../../config/apiConfig';

export interface AssistantOptionProps {
  assistant: AssistantOptionType;
  isSelected: boolean;
  onClick: () => void;
}

export default function AssistantOption({
  assistant,
  isSelected,
  onClick,
}: AssistantOptionProps) {
  // Resolve absolute avatar URL
  let avatarSrc = assistant.avatarUrl;
  if (avatarSrc && !avatarSrc.startsWith('http')) {
    avatarSrc = resolveAgainstApiBase(avatarSrc).toString();
  }

  return (
    <button
      type="button"
      onClick={onClick}
      className={`
        w-full flex items-center gap-3 px-4 py-3
        text-left text-sm
        hover:bg-gray-50 focus:outline-none focus:bg-gray-50
        transition-colors duration-150
        ${isSelected ? 'bg-blue-50' : ''}
      `}
      role="option"
      aria-selected={isSelected}
    >
      {/* Avatar */}
      {avatarSrc && (
        <img
          src={avatarSrc}
          alt={`${assistant.name} avatar`}
          className="h-9 w-9 rounded-full object-cover flex-shrink-0"
        />
      )}

      {/* Assistant Name */}
      <span className="flex-1 truncate text-gray-900 text-base font-medium">
        {assistant.name}
      </span>

      {/* Selection Indicator */}
      {isSelected && (
        <svg
          className="h-4 w-4 text-blue-600 flex-shrink-0"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            d="M5 13l4 4L19 7"
          />
        </svg>
      )}
    </button>
  );
} 
