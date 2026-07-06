import { useEffect, useState } from 'react';
import { api } from '../../../../services/api';
import type { RuntimeVoicesResponseDto } from '../../../../types/settings';

const SPEECH_SYNTHESIS_SERVICE_ID = 'SpeechSynthesis';

const SELECT_CLASS =
  'w-full rounded border border-gray-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-gray-100 disabled:text-gray-600';

interface BuiltinVoicePickerProps {
  value: string;
  onChange: (voiceId: string) => void;
  disabled?: boolean;
  hasError?: boolean;
  allowEmpty?: boolean;
}

export function BuiltinVoicePicker({
  value,
  onChange,
  disabled = false,
  hasError = false,
  allowEmpty = false,
}: BuiltinVoicePickerProps) {
  const [voices, setVoices] = useState<string[] | null>(null);
  const [modelId, setModelId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadFailed, setLoadFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      setLoading(true);
      setLoadFailed(false);
      const outcome = await api.settings.localModels.voicesOutcome(SPEECH_SYNTHESIS_SERVICE_ID);
      if (cancelled) {
        return;
      }
      if (outcome.kind === 'available') {
        const payload = outcome.payload as RuntimeVoicesResponseDto;
        const list = Array.isArray(payload.voices)
          ? payload.voices.map((voice) => voice.trim()).filter((voice) => voice.length > 0)
          : [];
        setModelId(typeof payload.modelId === 'string' ? payload.modelId : null);
        setVoices(list);
        setLoadFailed(list.length === 0 && !allowEmpty);
      } else {
        setVoices(null);
        setModelId(null);
        setLoadFailed(true);
      }
      setLoading(false);
    })();
    return () => {
      cancelled = true;
    };
  }, [allowEmpty]);

  const selectClassName = `${SELECT_CLASS} ${hasError ? 'border-red-500' : 'border-gray-300'}`;
  const valueInList = voices?.includes(value) ?? false;
  const showOrphanValue = value.trim().length > 0 && voices !== null && !valueInList;

  if (loading) {
    return (
      <select disabled className={selectClassName} aria-busy="true" aria-label="Speaker voice">
        <option>Loading speaker voices…</option>
      </select>
    );
  }

  if (loadFailed || !voices) {
    return (
      <div className="space-y-2">
        <select disabled className={selectClassName} aria-label="Speaker voice">
          <option>Runtime voices unavailable</option>
        </select>
        <p className="text-xs text-amber-800">
          Speaker voices could not be loaded{modelId ? ` for ${modelId}` : ''}. Load a TTS model first, then refresh
          this page.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-1">
      <select
        value={value}
        onChange={(event) => onChange(event.target.value)}
        disabled={disabled}
        className={selectClassName}
        aria-invalid={hasError ? true : undefined}
        aria-label="Speaker voice"
      >
        {allowEmpty ? <option value="">Use model default speaker…</option> : <option value="">Select a speaker…</option>}
        {showOrphanValue ? (
          <option value={value}>
            {value} (not in runtime voice list)
          </option>
        ) : null}
        {voices.map((voice) => (
          <option key={voice} value={voice}>
            {voice}
          </option>
        ))}
      </select>
      {showOrphanValue ? (
        <p className="text-xs text-amber-800">
          The saved voice <span className="font-mono">{value}</span> is not offered by the loaded model. Choose a speaker
          from the list.
        </p>
      ) : null}
    </div>
  );
}
