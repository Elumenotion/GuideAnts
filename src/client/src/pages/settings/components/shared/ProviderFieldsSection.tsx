import type { ReactNode } from 'react';
import type { ProviderEditorStateDto, ProviderFieldMetadataDto } from '../../../../types/settings';
import { getProviderFieldHelpText, getProviderFieldLabel } from '../../constants/displayLabels';
import { EnumSelect } from '../inputs/EnumSelect';
import { IntInput } from '../inputs/IntInput';
import { SecretInput } from '../inputs/SecretInput';
import { UrlInput } from '../inputs/UrlInput';
import { LocalTtsVoiceNameField } from '../inputs/LocalTtsVoiceNameField';

const LOCAL_TTS_PROVIDER_ID = 'SpeechSynthesis.LocalTts.Http';

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
  const blocked = !provider.connectionConfigured;

  const renderField = (metadata: ProviderFieldMetadataDto) => {
    const fieldDto = provider.fields[metadata.name];
    const rawValue =
      (draft[metadata.name] as string | undefined) ?? fieldDto?.value ?? '';
    const err = fieldErrors[metadata.name];
    const label = getProviderFieldLabel(provider.providerId, metadata.name);
    const helpText = getProviderFieldHelpText(provider.providerId, metadata.name);

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
      case 'text':
        if (provider.providerId === LOCAL_TTS_PROVIDER_ID && metadata.name === 'VoiceName') {
          control = (
            <LocalTtsVoiceNameField
              value={rawValue}
              onChange={(voiceId) => onValueChange(voiceId)}
              disabled={blocked}
              hasError={!!err}
            />
          );
          break;
        }
        // fall through for other text fields
        control = (
          <input
            value={rawValue}
            onChange={(event) => onValueChange(event.target.value)}
            className={`w-full rounded border px-3 py-2 text-sm ${err ? 'border-red-500' : 'border-gray-300'}`}
            aria-invalid={err ? true : undefined}
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
        <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600">{label}</label>
        <div className={err ? 'rounded ring-1 ring-red-500 ring-offset-1' : undefined}>{control}</div>
        {err ? (
          <p className="text-xs text-red-600" role="alert">
            {err}
          </p>
        ) : null}
        {helpText ? <p className="text-xs text-gray-500">{helpText}</p> : null}
      </div>
    );
  };

  return (
    <div className="space-y-4">
      {blocked ? (
        <div className="rounded border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900">
          Configure this provider connection first: {provider.connectionMissingFields.join(', ')}.
        </div>
      ) : !provider.hasExplicitMode ? (
        <div className="rounded border border-blue-200 bg-blue-50 p-3 text-sm text-blue-900">
          Saving this provider will create an explicit service mode before activation.
        </div>
      ) : null}
      {operativeMetadata.length > 0 ? (
        operativeMetadata.map((m) => renderField(m))
      ) : (
        <p className="text-sm text-gray-600">No service-mode fields are editable for this provider.</p>
      )}
    </div>
  );
}
