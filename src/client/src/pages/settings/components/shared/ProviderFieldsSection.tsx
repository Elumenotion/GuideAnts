import type { ReactNode } from 'react';
import type { ProviderEditorStateDto, ProviderFieldMetadataDto } from '../../../../types/settings';
import { EnumSelect } from '../inputs/EnumSelect';
import { IntInput } from '../inputs/IntInput';
import { SecretInput } from '../inputs/SecretInput';
import { UrlInput } from '../inputs/UrlInput';

interface ProviderFieldsSectionProps {
  provider: ProviderEditorStateDto;
  draft: Record<string, unknown>;
  fieldErrors: Record<string, string>;
  onPatch: (patch: Record<string, unknown>) => void;
  onClearFieldError?: (fieldName: string) => void;
}

export function ProviderFieldsSection({
  provider,
  draft,
  fieldErrors,
  onPatch,
  onClearFieldError,
}: ProviderFieldsSectionProps): ReactNode {
  const operativeMetadata = provider.fieldMetadata.filter((field) =>
    provider.operativeFields.includes(field.name)
  );

  const renderField = (metadata: ProviderFieldMetadataDto) => {
    const fieldDto = provider.fields[metadata.name];
    const rawValue =
      (draft[metadata.name] as string | undefined) ?? fieldDto?.value ?? '';
    const err = fieldErrors[metadata.name];

    const onValueChange = (value: string | number | ''): void => {
      onClearFieldError?.(metadata.name);
      onPatch({ [metadata.name]: value === '' ? '' : String(value) });
    };

    let control: ReactNode;
    switch (metadata.kind) {
      case 'secret': {
        const hasStored = fieldDto?.hasValue === true && fieldDto?.isSecret === true;
        control = (
          <SecretInput
            value={rawValue}
            onChange={(value) => onValueChange(value)}
            storedHasValue={hasStored}
          />
        );
        break;
      }
      case 'url':
        control = <UrlInput value={rawValue} onChange={(value) => onValueChange(value)} />;
        break;
      case 'int':
        control = (
          <IntInput value={rawValue === '' ? '' : Number(rawValue)} onChange={(value) => onValueChange(value)} />
        );
        break;
      case 'enum':
        control = (
          <EnumSelect
            value={rawValue}
            options={metadata.enumOptions && metadata.enumOptions.length > 0 ? metadata.enumOptions : ['']}
            onChange={(value) => onValueChange(value)}
          />
        );
        break;
      default:
        control = (
          <input
            value={rawValue}
            onChange={(event) => onValueChange(event.target.value)}
            className={`w-full rounded border px-3 py-2 text-sm ${err ? 'border-red-500' : 'border-gray-300'}`}
            aria-invalid={err ? true : undefined}
          />
        );
        break;
    }

    return (
      <div key={metadata.name} className="space-y-1">
        <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600">{metadata.label}</label>
        <div className={err ? 'rounded ring-1 ring-red-500 ring-offset-1' : undefined}>{control}</div>
        {err ? (
          <p className="text-xs text-red-600" role="alert">
            {err}
          </p>
        ) : null}
        <p className="text-xs text-gray-500">{metadata.helpText}</p>
      </div>
    );
  };

  return <div className="space-y-4">{operativeMetadata.map((m) => renderField(m))}</div>;
}
