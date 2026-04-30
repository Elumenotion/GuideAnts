import type { GeminiOptionalServicesFormState } from '../types';

interface GeminiOptionalServicesStepProps {
  value: GeminiOptionalServicesFormState;
  errors: Record<string, string>;
  onChange: (patch: Partial<GeminiOptionalServicesFormState>) => void;
}

interface ServiceCardProps {
  title: string;
  description: string;
  enabled: boolean;
  onToggle: (enabled: boolean) => void;
  children: React.ReactNode;
}

function ServiceCard({ title, description, enabled, onToggle, children }: ServiceCardProps) {
  return (
    <div className="rounded border border-gray-200 bg-white p-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h4 className="text-sm font-semibold text-gray-900">{title}</h4>
          <p className="mt-1 text-xs text-gray-600">{description}</p>
        </div>
        <label className="inline-flex items-center gap-2 text-xs font-medium text-gray-700">
          <input
            type="checkbox"
            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            checked={enabled}
            onChange={(event) => onToggle(event.target.checked)}
          />
          Configure now
        </label>
      </div>
      {enabled ? <div className="mt-4 space-y-3">{children}</div> : null}
    </div>
  );
}

interface FieldProps {
  id: string;
  label: string;
  value: string;
  type?: 'text' | 'password' | 'number';
  placeholder?: string;
  onChange: (next: string) => void;
  error?: string;
  helperText?: React.ReactNode;
}

function Field({
  id,
  label,
  value,
  type = 'text',
  placeholder,
  onChange,
  error,
  helperText,
}: FieldProps) {
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-xs font-semibold uppercase tracking-wide text-gray-600">
        {label}
      </label>
      <input
        id={id}
        type={type}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
        className={`w-full rounded border px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
          error ? 'border-red-500' : 'border-gray-300'
        }`}
      />
      {helperText ? <p className="text-xs text-gray-500">{helperText}</p> : null}
      {error ? <p className="text-xs text-red-700">{error}</p> : null}
    </div>
  );
}

export function GeminiOptionalServicesStep({
  value,
  errors,
  onChange,
}: GeminiOptionalServicesStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Optional Google Gemini services</h3>
        <p className="mt-1 text-sm text-gray-600">
          Basic chat works with the Gemini connection and at least one Gemini model. These service routes are optional.
        </p>
      </div>

      <ServiceCard
        title="Embeddings"
        description="Route Embeddings through Gemini."
        enabled={value.enableEmbeddings}
        onToggle={(enabled) => onChange({ enableEmbeddings: enabled })}
      >
        <Field
          id="gemini-embeddings-model-id"
          label="Embedding Model ID"
          value={value.embeddingsModelId}
          onChange={(next) => onChange({ embeddingsModelId: next })}
          error={errors.embeddingsModelId}
          placeholder="gemini-embedding-2"
        />
        <Field
          id="gemini-embeddings-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.embeddingsTimeoutSeconds}
          onChange={(next) => onChange({ embeddingsTimeoutSeconds: next })}
          error={errors.embeddingsTimeoutSeconds}
        />
      </ServiceCard>

      <ServiceCard
        title="Image Generation"
        description="Route Image Generation through Gemini."
        enabled={value.enableImages}
        onToggle={(enabled) => onChange({ enableImages: enabled })}
      >
        <Field
          id="gemini-image-model-id"
          label="Image Model ID"
          value={value.imagesModelId}
          onChange={(next) => onChange({ imagesModelId: next })}
          error={errors.imagesModelId}
          placeholder="gemini-2.5-flash-image"
        />
        <Field
          id="gemini-image-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.imagesTimeoutSeconds}
          onChange={(next) => onChange({ imagesTimeoutSeconds: next })}
          error={errors.imagesTimeoutSeconds}
        />
      </ServiceCard>

      <ServiceCard
        title="Speech Transcription"
        description="Route Speech Transcription through Gemini."
        enabled={value.enableSpeechTranscription}
        onToggle={(enabled) => onChange({ enableSpeechTranscription: enabled })}
      >
        <Field
          id="gemini-transcription-model-id"
          label="Transcription Model ID"
          value={value.speechTranscriptionModelId}
          onChange={(next) => onChange({ speechTranscriptionModelId: next })}
          error={errors.speechTranscriptionModelId}
          placeholder="gemini-2.5-flash"
        />
        <Field
          id="gemini-transcription-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.speechTranscriptionTimeoutSeconds}
          onChange={(next) => onChange({ speechTranscriptionTimeoutSeconds: next })}
          error={errors.speechTranscriptionTimeoutSeconds}
        />
      </ServiceCard>

      <ServiceCard
        title="Speech Synthesis"
        description="Route Speech Synthesis through Gemini."
        enabled={value.enableSpeechSynthesis}
        onToggle={(enabled) => onChange({ enableSpeechSynthesis: enabled })}
      >
        <Field
          id="gemini-tts-model-id"
          label="TTS Model ID"
          value={value.speechSynthesisModelId}
          onChange={(next) => onChange({ speechSynthesisModelId: next })}
          error={errors.speechSynthesisModelId}
          placeholder="gemini-3.1-flash-tts-preview"
        />
        <Field
          id="gemini-tts-voice-name"
          label="Voice Name"
          value={value.speechSynthesisVoiceName}
          onChange={(next) => onChange({ speechSynthesisVoiceName: next })}
          error={errors.speechSynthesisVoiceName}
          placeholder="Kore"
        />
        <Field
          id="gemini-tts-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.speechSynthesisTimeoutSeconds}
          onChange={(next) => onChange({ speechSynthesisTimeoutSeconds: next })}
          error={errors.speechSynthesisTimeoutSeconds}
        />
      </ServiceCard>
    </div>
  );
}

