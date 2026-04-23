import { ConversationStartersEditor } from '../../common/ConversationStartersEditor';

interface ConversationStartersProps {
  starters: string[];
  onChange: (starters: string[]) => void;
}

export function ConversationStarters({ starters, onChange }: ConversationStartersProps) {
  return (
    <div className="space-y-4">
      <h2 className="text-lg font-semibold text-gray-900">Conversation Starters</h2>
      <p className="text-sm text-gray-600">
        Suggested prompts that will be displayed to users when they start a conversation with this guide.
      </p>
      
      <ConversationStartersEditor
        starters={starters}
        onChange={onChange}
      />
    </div>
  );
}


