import type { LocalAiOptionalServicesFormState } from '../types';

interface LocalAiOptionalServicesStepProps {
  value: LocalAiOptionalServicesFormState;
  errors: Record<string, string>;
  onChange: (patch: Partial<LocalAiOptionalServicesFormState>) => void;
}

interface ServiceCardProps {
  title: string;
  description: string;
  infraKey: string;
  enabled: boolean;
  onToggle: (enabled: boolean) => void;
  children: React.ReactNode;
}

function ServiceCard({ title, description, infraKey, enabled, onToggle, children }: ServiceCardProps) {
  return (
    <div className="rounded border border-gray-200 bg-white p-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h4 className="text-sm font-semibold text-gray-900">{title}</h4>
          <p className="mt-1 text-xs text-gray-600">{description}</p>
          <p className="mt-0.5 font-mono text-[11px] text-gray-400">Requires: {infraKey}</p>
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
  type?: 'text' | 'number';
  placeholder?: string;
  onChange: (next: string) => void;
  error?: string;
  helperText?: string;
}

function Field({ id, label, value, type = 'text', placeholder, onChange, error, helperText }: FieldProps) {
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

export function LocalAiOptionalServicesStep({ value, errors, onChange }: LocalAiOptionalServicesStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Local AI optional services</h3>
        <p className="mt-1 text-sm text-gray-600">
          These services use local container-hosted backends. Each service requires the corresponding
          infrastructure URL to be configured in the container environment before activation.
          You can enable them now to pre-configure the settings.
        </p>
      </div>

      <ServiceCard
        title="Embeddings"
        description="Route embeddings through the local Emb service (LocalEmb.Http)."
        infraKey="LocalServiceHosts:EmbeddingsBaseUrl"
        enabled={value.enableEmbeddings}
        onToggle={(enabled) => onChange({ enableEmbeddings: enabled })}
      >
        <Field
          id="local-emb-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.embeddingsTimeoutSeconds}
          onChange={(next) => onChange({ embeddingsTimeoutSeconds: next })}
          error={errors.embeddingsTimeoutSeconds}
          placeholder="300"
        />
        <Field
          id="local-emb-min-interval"
          label="Min Interval (ms)"
          type="number"
          value={value.embeddingsLocalMinIntervalMs}
          onChange={(next) => onChange({ embeddingsLocalMinIntervalMs: next })}
          error={errors.embeddingsLocalMinIntervalMs}
          placeholder="100"
          helperText="Minimum milliseconds between requests to throttle the local service."
        />
      </ServiceCard>

      <ServiceCard
        title="Image Generation"
        description="Route image generation through the local Stable Diffusion service (LocalSd.Http)."
        infraKey="LocalServiceHosts:ImageGenerationBaseUrl"
        enabled={value.enableImages}
        onToggle={(enabled) => onChange({ enableImages: enabled })}
      >
        <Field
          id="local-img-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.imagesTimeoutSeconds}
          onChange={(next) => onChange({ imagesTimeoutSeconds: next })}
          error={errors.imagesTimeoutSeconds}
          placeholder="600"
          helperText="Image generation may be slow. A higher timeout prevents premature cancellation."
        />
        <Field
          id="local-img-output-format"
          label="Output Format"
          value={value.imagesLocalOutputFormat}
          onChange={(next) => onChange({ imagesLocalOutputFormat: next })}
          error={errors.imagesLocalOutputFormat}
          placeholder="png"
          helperText="e.g. png or webp. Leave blank to use the service default."
        />
      </ServiceCard>

      <ServiceCard
        title="Speech Transcription"
        description="Route ASR through the local speech transcription service (LocalAsr.Http)."
        infraKey="LocalServiceHosts:SpeechTranscriptionBaseUrl"
        enabled={value.enableSpeechTranscription}
        onToggle={(enabled) => onChange({ enableSpeechTranscription: enabled })}
      >
        <Field
          id="local-asr-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.speechTranscriptionTimeoutSeconds}
          onChange={(next) => onChange({ speechTranscriptionTimeoutSeconds: next })}
          error={errors.speechTranscriptionTimeoutSeconds}
          placeholder="300"
        />
      </ServiceCard>

      <ServiceCard
        title="Speech Synthesis"
        description="Route TTS through the local speech synthesis service (LocalTts.Http)."
        infraKey="LocalServiceHosts:SpeechSynthesisBaseUrl"
        enabled={value.enableSpeechSynthesis}
        onToggle={(enabled) => onChange({ enableSpeechSynthesis: enabled })}
      >
        <Field
          id="local-tts-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.speechSynthesisTimeoutSeconds}
          onChange={(next) => onChange({ speechSynthesisTimeoutSeconds: next })}
          error={errors.speechSynthesisTimeoutSeconds}
          placeholder="300"
        />
      </ServiceCard>

      <ServiceCard
        title="Document Intelligence"
        description="Route document conversion through the local Docling service (LocalDocling.Http)."
        infraKey="LocalServiceHosts:DocumentIntelligenceBaseUrl"
        enabled={value.enableDocumentIntelligence}
        onToggle={(enabled) => onChange({ enableDocumentIntelligence: enabled })}
      >
        <Field
          id="local-docling-timeout"
          label="Timeout Seconds"
          type="number"
          value={value.documentIntelligenceTimeoutSeconds}
          onChange={(next) => onChange({ documentIntelligenceTimeoutSeconds: next })}
          error={errors.documentIntelligenceTimeoutSeconds}
          placeholder="600"
        />
        <Field
          id="local-docling-concurrent"
          label="Max Concurrent Conversions"
          type="number"
          value={value.documentIntelligenceMaxConcurrentConversions}
          onChange={(next) => onChange({ documentIntelligenceMaxConcurrentConversions: next })}
          error={errors.documentIntelligenceMaxConcurrentConversions}
          placeholder="2"
          helperText="Maximum number of documents being converted simultaneously."
        />
        <Field
          id="local-docling-poll-interval"
          label="Async Status Poll Interval (ms)"
          type="number"
          value={value.documentIntelligenceAsyncStatusPollIntervalMs}
          onChange={(next) => onChange({ documentIntelligenceAsyncStatusPollIntervalMs: next })}
          error={errors.documentIntelligenceAsyncStatusPollIntervalMs}
          placeholder="2000"
          helperText="How often to poll for conversion status."
        />
      </ServiceCard>
    </div>
  );
}
