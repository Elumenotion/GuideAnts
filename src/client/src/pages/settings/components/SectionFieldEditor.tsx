import { SettingsSectionDto, SettingsSectionPropertyDefinitionDto } from '../../../types/settings';
import { getInputTextValue, humanizeKey, parseFieldValue, SECRET_MASK } from '../utils';

interface SectionFieldEditorProps {
  sectionName: string;
  section: SettingsSectionDto;
  property: SettingsSectionPropertyDefinitionDto;
  value: unknown;
  disabled: boolean;
  onValueChange: (sectionName: string, key: string, value: unknown) => void;
}

export function SectionFieldEditor({
  sectionName,
  section,
  property,
  value,
  disabled,
  onValueChange,
}: SectionFieldEditorProps) {
  const fieldInputId = `${sectionName}-${property.name}`;

  return (
    <div className="space-y-2">
      <label htmlFor={fieldInputId} className="block text-xs font-medium uppercase tracking-wide text-gray-600">
        {humanizeKey(property.name)}
      </label>

      {property.valueType === 'bool' ? (
        <label className="inline-flex items-center gap-2 text-sm text-gray-800">
          <input
            id={fieldInputId}
            type="checkbox"
            checked={Boolean(value)}
            onChange={(event) => onValueChange(sectionName, property.name, event.target.checked)}
            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            disabled={disabled}
          />
          Enabled
        </label>
      ) : (
        <input
          id={fieldInputId}
          type={property.isSecret ? 'password' : property.valueType === 'int' ? 'number' : 'text'}
          value={getInputTextValue(value)}
          onChange={(event) => onValueChange(sectionName, property.name, parseFieldValue(event.target.value, property))}
          className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-gray-100"
          autoComplete="off"
          spellCheck={false}
          disabled={disabled}
        />
      )}

      {property.isSecret && (
        <p className="text-xs text-gray-500">
          {section.secretHasValue?.[property.name]
            ? `A secret is stored. Keep ${SECRET_MASK} to preserve it, or type a new value.`
            : 'No secret stored yet. Enter a value to save one.'}
        </p>
      )}
    </div>
  );
}
