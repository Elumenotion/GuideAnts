// No React import needed for this component
import { AssistantDropdown } from './assistant-selector';

export interface AssistantOption {
  name: string;
  model?: string;
  avatarUrl: string;
}

interface AssistantSelectorProps {
  assistants: AssistantOption[];
  selectedName: string;
  onSelect: (assistantName: string) => void;
  className?: string;
  disabled?: boolean;
  'data-tour-id'?: string;
}

/**
 * Enhanced assistant selector with search functionality, rich avatar display,
 * and custom dropdown UI. Provides keyboard and screen reader accessibility.
 */
export default function AssistantSelector({
  assistants,
  selectedName,
  onSelect,
  className = '',
  disabled = false,
  'data-tour-id': dataTourId,
}: AssistantSelectorProps) {
  return (
    <AssistantDropdown
      assistants={assistants}
      selectedName={selectedName}
      onSelect={onSelect}
      className={className}
      disabled={disabled}
      data-tour-id={dataTourId}
    />
  );
} 