import { useEffect, useState } from 'react';
import { api } from '../../../../services/api';
import type { VoicePackResponseDto, VoicePackVoiceDto } from '../../../../types/settings';

const SPEECH_SYNTHESIS_SERVICE_ID = 'SpeechSynthesis';

const SELECT_CLASS =
  'w-full rounded border border-gray-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:bg-gray-100 disabled:text-gray-600';

interface VoicePackVoicePickerProps {
  value: string;
  onChange: (voiceId: string) => void;
  disabled?: boolean;
  hasError?: boolean;
  allowEmpty?: boolean;
}

function formatVoiceOptionLabel(voice: VoicePackVoiceDto): string {
  const meta = [voice.displayName, voice.language, voice.accent].filter(Boolean).join(' · ');
  return meta ? `${voice.voiceId} — ${meta}` : voice.voiceId;
}

export function VoicePackVoicePicker({
  value,
  onChange,
  disabled = false,
  hasError = false,
  allowEmpty = false,
}: VoicePackVoicePickerProps) {
  const [voices, setVoices] = useState<VoicePackVoiceDto[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadFailed, setLoadFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      setLoading(true);
      setLoadFailed(false);
      const outcome = await api.settings.localModels.voicePackOutcome(SPEECH_SYNTHESIS_SERVICE_ID);
      if (cancelled) {
        return;
      }
      if (outcome.kind === 'available') {
        const payload = outcome.payload as VoicePackResponseDto;
        const list = Array.isArray(payload.voices) ? payload.voices : [];
        setVoices(list);
        setLoadFailed(list.length === 0);
      } else {
        setVoices(null);
        setLoadFailed(true);
      }
      setLoading(false);
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const selectClassName = `${SELECT_CLASS} ${hasError ? 'border-red-500' : 'border-gray-300'}`;
  const valueInPack = voices?.some((voice) => voice.voiceId === value) ?? false;
  const showOrphanValue = value.trim().length > 0 && voices !== null && !valueInPack;

  if (loading) {
    return (
      <select disabled className={selectClassName} aria-busy="true" aria-label="Reference voice">
        <option>Loading voice presets…</option>
      </select>
    );
  }

  if (loadFailed || !voices) {
    return (
      <div className="space-y-2">
        <select disabled className={selectClassName} aria-label="Reference voice">
          <option>Voice pack unavailable</option>
        </select>
        <p className="text-xs text-amber-800">
          Voice pack presets could not be loaded. Check that the local TTS service is running, then refresh this page.
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
        aria-label="Reference voice"
      >
        <option value="">{allowEmpty ? 'No reference clip (model default)…' : 'Select a reference voice…'}</option>
        {showOrphanValue ? (
          <option value={value}>
            {value} (not in voice pack)
          </option>
        ) : null}
        {voices.map((voice) => (
          <option key={voice.voiceId} value={voice.voiceId}>
            {formatVoiceOptionLabel(voice)}
          </option>
        ))}
      </select>
      {showOrphanValue ? (
        <p className="text-xs text-amber-800">
          The saved voice <span className="font-mono">{value}</span> is not in the baked pack. Choose a preset from the list.
        </p>
      ) : null}
    </div>
  );
}
