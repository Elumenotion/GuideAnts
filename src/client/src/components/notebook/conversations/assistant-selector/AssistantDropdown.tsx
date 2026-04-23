import { useState, useMemo } from 'react';
import { CustomDropdown, DropdownPanel, DropdownSearch } from '../../../common/dropdowns';
import AssistantButton from './AssistantButton';
import AssistantOption from './AssistantOption';
import { AssistantOption as AssistantOptionType } from '../AssistantSelector';

export interface AssistantDropdownProps {
  assistants: AssistantOptionType[];
  selectedName: string;
  onSelect: (assistantName: string) => void;
  className?: string;
  disabled?: boolean;
  'data-tour-id'?: string;
}

export default function AssistantDropdown({
  assistants,
  selectedName,
  onSelect,
  className = '',
  disabled = false,
  'data-tour-id': dataTourId,
}: AssistantDropdownProps) {
  const [searchQuery, setSearchQuery] = useState('');

  const selectedAssistant =
    assistants.find((a) => a.name === selectedName) ??
    (selectedName ? { name: selectedName, model: '', avatarUrl: '' } : assistants[0]);

  // Filter assistants based on search query
  const filteredAssistants = useMemo(() => {
    if (!searchQuery.trim()) return assistants;
    
    const query = searchQuery.toLowerCase().trim();
    return assistants.filter((assistant) =>
      assistant.name.toLowerCase().includes(query) ||
      (assistant.model && assistant.model.toLowerCase().includes(query))
    );
  }, [assistants, searchQuery]);

  const handleSelect = (assistantName: string) => {
    onSelect(assistantName);
    setSearchQuery(''); // Clear search when selecting
  };

  const handleOpenChange = (open: boolean) => {
    if (!open) {
      setSearchQuery(''); // Clear search when closing
    }
  };

  return (
    <CustomDropdown
      className={className}
      disabled={disabled}
      onOpenChange={handleOpenChange}
      data-tour-id={dataTourId}
      trigger={
        <AssistantButton
          assistant={selectedAssistant}
          disabled={disabled}
        />
      }
    >
      <DropdownPanel width="w-80" maxHeight="max-h-96">
        <DropdownSearch
          value={searchQuery}
          onChange={setSearchQuery}
          placeholder="Search guides by name"
        />
        
        <div className="overflow-y-auto max-h-80">
          {filteredAssistants.length > 0 ? (
            filteredAssistants.map((assistant) => (
              <AssistantOption
                key={assistant.name}
                assistant={assistant}
                isSelected={assistant.name === selectedName}
                onClick={() => handleSelect(assistant.name)}
              />
            ))
          ) : (
            <div className="px-4 py-8 text-center text-gray-500 text-base">
              No guides found matching "{searchQuery}"
            </div>
          )}
        </div>
      </DropdownPanel>
    </CustomDropdown>
  );
} 
