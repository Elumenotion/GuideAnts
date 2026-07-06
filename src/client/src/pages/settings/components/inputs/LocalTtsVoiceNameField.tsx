import { useEffect, useState } from 'react';
import { api } from '../../../../services/api';
import type { LocalModelCatalogEntryDto, LocalModelCatalogResponseDto } from '../../../../types/settings';
import { BuiltinVoicePicker } from './BuiltinVoicePicker';
import { VoicePackVoicePicker } from './VoicePackVoicePicker';

const SPEECH_SYNTHESIS_SERVICE_ID = 'SpeechSynthesis';

type TtsReadiness = {
  catalogEntryId?: string | null;
};

interface LocalTtsVoiceNameFieldProps {
  value: string;
  onChange: (voiceId: string) => void;
  disabled?: boolean;
  hasError?: boolean;
}

function resolveVoiceInput(
  catalogEntryId: string | null | undefined,
  entries: LocalModelCatalogEntryDto[] | undefined
): LocalModelCatalogEntryDto['voiceInput'] | 'pending' {
  if (!catalogEntryId) {
    return 'pending';
  }
  const entry = entries?.find((item) => item.id === catalogEntryId);
  return entry?.voiceInput ?? 'pending';
}

export function LocalTtsVoiceNameField({
  value,
  onChange,
  disabled = false,
  hasError = false,
}: LocalTtsVoiceNameFieldProps) {
  const [voiceInput, setVoiceInput] = useState<LocalModelCatalogEntryDto['voiceInput'] | 'pending' | 'error'>(
    'pending'
  );

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      setVoiceInput('pending');
      const [catalogOutcome, readinessOutcome] = await Promise.all([
        api.settings.localModels.catalogOutcome(SPEECH_SYNTHESIS_SERVICE_ID),
        api.settings.localModels.runtimeReadinessOutcome(SPEECH_SYNTHESIS_SERVICE_ID),
      ]);
      if (cancelled) {
        return;
      }
      if (catalogOutcome.kind !== 'available' || readinessOutcome.kind !== 'available') {
        setVoiceInput('error');
        return;
      }
      const catalog = catalogOutcome.payload as LocalModelCatalogResponseDto;
      const readiness = readinessOutcome.payload as TtsReadiness;
      setVoiceInput(resolveVoiceInput(readiness.catalogEntryId, catalog.entries));
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  if (voiceInput === 'pending') {
    return (
      <input
        disabled
        value=""
        placeholder="Resolving voice controls for loaded model…"
        className="w-full rounded border border-gray-300 bg-gray-100 px-3 py-2 text-sm text-gray-600"
        aria-busy="true"
        aria-label="Voice"
      />
    );
  }

  if (voiceInput === 'error' || !voiceInput) {
    return (
      <div className="space-y-1">
        <input
          value={value}
          onChange={(event) => onChange(event.target.value)}
          disabled={disabled}
          className={`w-full rounded border px-3 py-2 text-sm ${hasError ? 'border-red-500' : 'border-gray-300'}`}
          aria-invalid={hasError ? true : undefined}
          aria-label="Voice"
        />
        <p className="text-xs text-amber-800">
          Could not resolve catalog voice controls. Load a TTS model, then refresh. You can still type a voice value
          manually.
        </p>
      </div>
    );
  }

  if (voiceInput === 'voice_pack') {
    return (
      <VoicePackVoicePicker value={value} onChange={onChange} disabled={disabled} hasError={hasError} />
    );
  }

  if (voiceInput === 'optional_ref') {
    return (
      <VoicePackVoicePicker
        value={value}
        onChange={onChange}
        disabled={disabled}
        hasError={hasError}
        allowEmpty
      />
    );
  }

  if (voiceInput === 'builtin') {
    return (
      <BuiltinVoicePicker value={value} onChange={onChange} disabled={disabled} hasError={hasError} />
    );
  }

  if (voiceInput === 'instruct') {
    return (
      <div className="space-y-1">
        <textarea
          value={value}
          onChange={(event) => onChange(event.target.value)}
          disabled={disabled}
          rows={3}
          className={`w-full rounded border px-3 py-2 text-sm ${hasError ? 'border-red-500' : 'border-gray-300'}`}
          aria-invalid={hasError ? true : undefined}
          aria-label="Voice design"
          placeholder="Describe the voice you want the model to generate…"
        />
        <p className="text-xs text-gray-500">
          Voice-design models synthesize from this description instead of a reference clip or built-in speaker.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-1">
      <input
        disabled
        value=""
        className="w-full rounded border border-gray-300 bg-gray-100 px-3 py-2 text-sm text-gray-600"
        aria-label="Voice"
      />
      <p className="text-xs text-gray-500">The loaded model does not expose a voice selection control.</p>
    </div>
  );
}
