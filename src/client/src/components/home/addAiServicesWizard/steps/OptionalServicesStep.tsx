import { SECRET_MASK } from '../constants';
import type { OptionalServicesFormState } from '../types';

interface OptionalServicesStepProps {
  value: OptionalServicesFormState;
  derivedCoreEndpoint: string;
  errors: Record<string, string>;
  onChange: (patch: Partial<OptionalServicesFormState>) => void;
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
  type?: 'text' | 'password';
  placeholder?: string;
  disabled?: boolean;
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
  disabled = false,
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
        disabled={disabled}
        placeholder={placeholder}
        className={`w-full rounded border px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
          error ? 'border-red-500' : 'border-gray-300'
        } ${disabled ? 'bg-gray-100 text-gray-500' : ''}`}
      />
      {helperText ? <p className="text-xs text-gray-500">{helperText}</p> : null}
      {error ? <p className="text-xs text-red-700">{error}</p> : null}
    </div>
  );
}

export function OptionalServicesStep({
  value,
  derivedCoreEndpoint,
  errors,
  onChange,
}: OptionalServicesStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Optional Microsot Foundry services</h3>
        <p className="mt-1 text-sm text-gray-600">
          Basic chat works with the core connection and at least one model. These services are optional and can be added now or later.
        </p>
      </div>

      <ServiceCard
        title="Embeddings"
        description="Configure cloud embeddings with a deployment name."
        enabled={value.enableEmbeddings}
        onToggle={(enabled) => onChange({ enableEmbeddings: enabled })}
      >
        <label className="inline-flex items-center gap-2 text-xs text-gray-700">
          <input
            type="checkbox"
            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            checked={value.linkEmbeddingsEndpointToCore}
            onChange={(event) => onChange({ linkEmbeddingsEndpointToCore: event.target.checked })}
          />
          Use core-derived endpoint
          {derivedCoreEndpoint ? <span className="font-mono text-gray-500">{derivedCoreEndpoint}</span> : null}
        </label>
        <Field
          id="embeddings-endpoint"
          label="Endpoint"
          value={value.embeddingsEndpoint}
          disabled={value.linkEmbeddingsEndpointToCore}
          onChange={(next) => onChange({ embeddingsEndpoint: next })}
          error={errors.embeddingsEndpoint}
          placeholder="https://your-resource.openai.azure.com/"
        />
        <Field
          id="embeddings-api-key"
          label="API key"
          type="password"
          value={value.embeddingsApiKey}
          onChange={(next) => onChange({ embeddingsApiKey: next })}
          error={errors.embeddingsApiKey}
          helperText={
            value.embeddingsApiKeyHasStoredValue
              ? <>A key is already stored. Keep <span className="font-mono">{SECRET_MASK}</span> to preserve it.</>
              : undefined
          }
        />
        <Field
          id="embeddings-deployment"
          label="Deployment"
          value={value.embeddingsDeployment}
          onChange={(next) => onChange({ embeddingsDeployment: next })}
          error={errors.embeddingsDeployment}
          placeholder="text-embedding-3-small"
        />
      </ServiceCard>

      <ServiceCard
        title="Image Generation"
        description="Configure image generation and edit deployment names."
        enabled={value.enableImages}
        onToggle={(enabled) => onChange({ enableImages: enabled })}
      >
        <label className="inline-flex items-center gap-2 text-xs text-gray-700">
          <input
            type="checkbox"
            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            checked={value.linkImagesEndpointToCore}
            onChange={(event) => onChange({ linkImagesEndpointToCore: event.target.checked })}
          />
          Use core-derived endpoint
          {derivedCoreEndpoint ? <span className="font-mono text-gray-500">{derivedCoreEndpoint}</span> : null}
        </label>
        <Field
          id="images-endpoint"
          label="Endpoint"
          value={value.imagesEndpoint}
          disabled={value.linkImagesEndpointToCore}
          onChange={(next) => onChange({ imagesEndpoint: next })}
          error={errors.imagesEndpoint}
          placeholder="https://your-resource.openai.azure.com/"
        />
        <Field
          id="images-api-key"
          label="API key"
          type="password"
          value={value.imagesApiKey}
          onChange={(next) => onChange({ imagesApiKey: next })}
          error={errors.imagesApiKey}
          helperText={
            value.imagesApiKeyHasStoredValue
              ? <>A key is already stored. Keep <span className="font-mono">{SECRET_MASK}</span> to preserve it.</>
              : undefined
          }
        />
        <Field
          id="images-api-version"
          label="API version"
          value={value.imagesApiVersion}
          onChange={(next) => onChange({ imagesApiVersion: next })}
          error={errors.imagesApiVersion}
        />
        <Field
          id="images-deployment"
          label="Generation deployment"
          value={value.imagesDeployment}
          onChange={(next) => onChange({ imagesDeployment: next })}
          error={errors.imagesDeployment}
          placeholder="gpt-image-1"
        />
        <Field
          id="images-edit-deployment"
          label="Edit deployment"
          value={value.imagesEditDeployment}
          onChange={(next) => onChange({ imagesEditDeployment: next })}
          error={errors.imagesEditDeployment}
          placeholder="gpt-image-1"
        />
      </ServiceCard>

      <ServiceCard
        title="Speech (Transcription and Synthesis)"
        description="Configure shared Speech connection used by both speech services."
        enabled={value.enableSpeech}
        onToggle={(enabled) => onChange({ enableSpeech: enabled })}
      >
        <Field
          id="speech-endpoint"
          label="Endpoint"
          value={value.speechEndpoint}
          onChange={(next) => onChange({ speechEndpoint: next })}
          error={errors.speechEndpoint}
          placeholder="https://my-speech-resource.cognitiveservices.azure.com/"
        />
        <Field
          id="speech-api-key"
          label="API key"
          type="password"
          value={value.speechApiKey}
          onChange={(next) => onChange({ speechApiKey: next })}
          error={errors.speechApiKey}
          helperText={
            value.speechApiKeyHasStoredValue
              ? <>A key is already stored. Keep <span className="font-mono">{SECRET_MASK}</span> to preserve it.</>
              : undefined
          }
        />
        <Field
          id="speech-region"
          label="Region"
          value={value.speechRegion}
          onChange={(next) => onChange({ speechRegion: next })}
          error={errors.speechRegion}
          placeholder="eastus"
        />
      </ServiceCard>

      <ServiceCard
        title="Document Intelligence"
        description="Configure markdown extraction with Azure Document Intelligence."
        enabled={value.enableDocumentIntelligence}
        onToggle={(enabled) => onChange({ enableDocumentIntelligence: enabled })}
      >
        <Field
          id="doc-endpoint"
          label="Endpoint"
          value={value.documentIntelligenceEndpoint}
          onChange={(next) => onChange({ documentIntelligenceEndpoint: next })}
          error={errors.documentIntelligenceEndpoint}
          placeholder="https://my-doc-int-resource.cognitiveservices.azure.com/"
        />
        <Field
          id="doc-api-key"
          label="API key"
          type="password"
          value={value.documentIntelligenceApiKey}
          onChange={(next) => onChange({ documentIntelligenceApiKey: next })}
          error={errors.documentIntelligenceApiKey}
          helperText={
            value.documentIntelligenceApiKeyHasStoredValue
              ? <>A key is already stored. Keep <span className="font-mono">{SECRET_MASK}</span> to preserve it.</>
              : undefined
          }
        />
      </ServiceCard>
    </div>
  );
}

