import type { HuggingFaceOptionalServicesFormState } from '../types';

interface HuggingFaceOptionalServicesStepProps {
  value: HuggingFaceOptionalServicesFormState;
  errors: Record<string, string>;
  onChange: (patch: Partial<HuggingFaceOptionalServicesFormState>) => void;
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
  placeholder?: string;
  onChange: (next: string) => void;
  error?: string;
}

function Field({ id, label, value, placeholder, onChange, error }: FieldProps) {
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-xs font-semibold uppercase tracking-wide text-gray-600">
        {label}
      </label>
      <input
        id={id}
        type="text"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
        className={`w-full rounded border px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
          error ? 'border-red-500' : 'border-gray-300'
        }`}
      />
      {error ? <p className="text-xs text-red-700">{error}</p> : null}
    </div>
  );
}

export function HuggingFaceOptionalServicesStep({
  value,
  errors,
  onChange,
}: HuggingFaceOptionalServicesStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Optional Hugging Face services</h3>
        <p className="mt-1 text-sm text-gray-600">
          Basic chat works with the Hugging Face connection and at least one HF chat model. These service routes are optional.
        </p>
      </div>

      <ServiceCard
        title="Embeddings"
        description="Route Embeddings through Hugging Face Inference."
        enabled={value.enableEmbeddings}
        onToggle={(enabled) => onChange({ enableEmbeddings: enabled })}
      >
        <Field
          id="hf-embeddings-model-id"
          label="Embedding Model ID"
          value={value.embeddingsModelId}
          onChange={(next) => onChange({ embeddingsModelId: next })}
          error={errors.embeddingsModelId}
          placeholder="microsoft/harrier-oss-v1-0.6b"
        />
        <Field
          id="hf-embeddings-timeout"
          label="Timeout Seconds"
          value={value.embeddingsTimeoutSeconds}
          onChange={(next) => onChange({ embeddingsTimeoutSeconds: next })}
          error={errors.embeddingsTimeoutSeconds}
          placeholder="300"
        />
      </ServiceCard>

      <ServiceCard
        title="Image Generation"
        description="Route Image Generation through Hugging Face Inference."
        enabled={value.enableImages}
        onToggle={(enabled) => onChange({ enableImages: enabled })}
      >
        <Field
          id="hf-image-t2i-model-id"
          label="Text-to-Image Model ID"
          value={value.imagesTextToImageModelId}
          onChange={(next) => onChange({ imagesTextToImageModelId: next })}
          error={errors.imagesTextToImageModelId}
          placeholder="Tongyi-MAI/Z-Image-Turbo"
        />
        <Field
          id="hf-image-i2i-model-id"
          label="Image-to-Image Model ID"
          value={value.imagesImageToImageModelId}
          onChange={(next) => onChange({ imagesImageToImageModelId: next })}
          error={errors.imagesImageToImageModelId}
          placeholder="black-forest-labs/FLUX.2-dev"
        />
        <Field
          id="hf-image-timeout"
          label="Timeout Seconds"
          value={value.imagesTimeoutSeconds}
          onChange={(next) => onChange({ imagesTimeoutSeconds: next })}
          error={errors.imagesTimeoutSeconds}
          placeholder="600"
        />
      </ServiceCard>

      <ServiceCard
        title="Speech Transcription"
        description="Route Speech Transcription through Hugging Face Inference."
        enabled={value.enableSpeechTranscription}
        onToggle={(enabled) => onChange({ enableSpeechTranscription: enabled })}
      >
        <Field
          id="hf-transcription-model-id"
          label="Transcription Model ID"
          value={value.speechTranscriptionModelId}
          onChange={(next) => onChange({ speechTranscriptionModelId: next })}
          error={errors.speechTranscriptionModelId}
          placeholder="openai/whisper-large-v3"
        />
        <Field
          id="hf-transcription-timeout"
          label="Timeout Seconds"
          value={value.speechTranscriptionTimeoutSeconds}
          onChange={(next) => onChange({ speechTranscriptionTimeoutSeconds: next })}
          error={errors.speechTranscriptionTimeoutSeconds}
          placeholder="300"
        />
      </ServiceCard>

      <ServiceCard
        title="Speech Synthesis"
        description="Route Speech Synthesis through Hugging Face Inference."
        enabled={value.enableSpeechSynthesis}
        onToggle={(enabled) => onChange({ enableSpeechSynthesis: enabled })}
      >
        <Field
          id="hf-tts-model-id"
          label="TTS Model ID"
          value={value.speechSynthesisModelId}
          onChange={(next) => onChange({ speechSynthesisModelId: next })}
          error={errors.speechSynthesisModelId}
          placeholder="ResembleAI/chatterbox"
        />
        <Field
          id="hf-tts-timeout"
          label="Timeout Seconds"
          value={value.speechSynthesisTimeoutSeconds}
          onChange={(next) => onChange({ speechSynthesisTimeoutSeconds: next })}
          error={errors.speechSynthesisTimeoutSeconds}
          placeholder="300"
        />
      </ServiceCard>
    </div>
  );
}
