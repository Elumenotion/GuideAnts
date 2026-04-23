import { KeyValueEditor } from '../../common/KeyValueEditor';
import { ContextOptionDto } from '../../../types/guides';

interface ContextOptionsProps {
  options: ContextOptionDto[];
  onChange: (options: ContextOptionDto[]) => void;
}

export function ContextOptions({ options, onChange }: ContextOptionsProps) {
  return (
    <div className="space-y-4">
      <h2 className="text-lg font-semibold text-gray-900">Context Options</h2>
      <p className="text-sm text-gray-600">
        Add custom context variables that can be used in instructions or API calls. These will be available to the guide at runtime.
      </p>
      
      <KeyValueEditor
        items={options}
        onChange={onChange}
        keyPlaceholder="Option key (e.g., api_base_url)"
        valuePlaceholder="Option value"
      />
    </div>
  );
}


