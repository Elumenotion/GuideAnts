import type { OpenAiOptionalServicesFormState } from '../types';

interface OpenAiOptionalServicesStepProps {
  value: OpenAiOptionalServicesFormState;
  errors: Record<string, string>;
  onChange: (patch: Partial<OpenAiOptionalServicesFormState>) => void;
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

export function OpenAiOptionalServicesStep({
  value,
  errors,
  onChange,
}: OpenAiOptionalServicesStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Optional OpenAI services</h3>
        <p className="mt-1 text-sm text-gray-600">
          Basic chat works with the OpenAI connection and at least one OpenAI model. These service routes are optional.
        </p>
      </div>

      <ServiceCard
        title="Embeddings"
        description="Route Embeddings through OpenAI."
        enabled={value.enableEmbeddings}
        onToggle={(enabled) => onChange({ enableEmbeddings: enabled })}
      >
        <Field
          id="openai-embeddings-model-id"
          label="Embedding Model ID"
          value={value.embeddingsModelId}
          onChange={(next) => onChange({ embeddingsModelId: next })}
          error={errors.embeddingsModelId}
          placeholder="text-embedding-3-small"
        />
        <Field
          id="openai-embeddings-dimensions"
          label="Dimensions (optional)"
          type="number"
          value={value.embeddingsDimensions}
          onChange={(next) => onChange({ embeddingsDimensions: next })}
          error={errors.embeddingsDimensions}
          helperText="Output embedding dimensions. Supported by text-embedding-3-* models only."
        />
        <Field
          id="openai-embeddings-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.embeddingsTimeoutSeconds}
          onChange={(next) => onChange({ embeddingsTimeoutSeconds: next })}
          error={errors.embeddingsTimeoutSeconds}
        />
      </ServiceCard>

      <ServiceCard
        title="Image Generation"
        description="Route Image Generation through OpenAI."
        enabled={value.enableImages}
        onToggle={(enabled) => onChange({ enableImages: enabled })}
      >
        <Field
          id="openai-image-model-id"
          label="Image Model ID"
          value={value.imagesModelId}
          onChange={(next) => onChange({ imagesModelId: next })}
          error={errors.imagesModelId}
          placeholder="gpt-image-1"
        />
        <Field
          id="openai-image-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.imagesTimeoutSeconds}
          onChange={(next) => onChange({ imagesTimeoutSeconds: next })}
          error={errors.imagesTimeoutSeconds}
        />
      </ServiceCard>

      <ServiceCard
        title="Speech Transcription"
        description="Route Speech Transcription through OpenAI Whisper."
        enabled={value.enableSpeechTranscription}
        onToggle={(enabled) => onChange({ enableSpeechTranscription: enabled })}
      >
        <Field
          id="openai-transcription-model-id"
          label="Transcription Model ID"
          value={value.speechTranscriptionModelId}
          onChange={(next) => onChange({ speechTranscriptionModelId: next })}
          error={errors.speechTranscriptionModelId}
          placeholder="whisper-1"
        />
        <Field
          id="openai-transcription-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.speechTranscriptionTimeoutSeconds}
          onChange={(next) => onChange({ speechTranscriptionTimeoutSeconds: next })}
          error={errors.speechTranscriptionTimeoutSeconds}
        />
      </ServiceCard>

      <ServiceCard
        title="Speech Synthesis"
        description="Route Speech Synthesis through OpenAI TTS."
        enabled={value.enableSpeechSynthesis}
        onToggle={(enabled) => onChange({ enableSpeechSynthesis: enabled })}
      >
        <Field
          id="openai-tts-model-id"
          label="TTS Model ID"
          value={value.speechSynthesisModelId}
          onChange={(next) => onChange({ speechSynthesisModelId: next })}
          error={errors.speechSynthesisModelId}
          placeholder="tts-1"
        />
        <Field
          id="openai-tts-voice-name"
          label="Voice Name"
          value={value.speechSynthesisVoiceName}
          onChange={(next) => onChange({ speechSynthesisVoiceName: next })}
          error={errors.speechSynthesisVoiceName}
          placeholder="alloy"
        />
        <Field
          id="openai-tts-timeout"
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
