import { useEffect, useState } from 'react';
import { DocumentIntelligenceEditor } from '../editors/document-intelligence/DocumentIntelligenceEditor';
import { EmbeddingsEditor } from '../editors/embeddings/EmbeddingsEditor';
import { ImageGenerationEditor } from '../editors/image-generation/ImageGenerationEditor';
import { SpeechSynthesisEditor } from '../editors/speech-synthesis/SpeechSynthesisEditor';
import { SpeechTranscriptionEditor } from '../editors/speech-transcription/SpeechTranscriptionEditor';

export type ServiceKey =
  | 'Embeddings'
  | 'ImageGeneration'
  | 'DocumentIntelligence'
  | 'SpeechTranscription'
  | 'SpeechSynthesis';

const services: Array<{ key: ServiceKey; label: string }> = [
  { key: 'SpeechTranscription', label: 'Speech Transcription' },
  { key: 'ImageGeneration', label: 'Image Generation' },
  { key: 'SpeechSynthesis', label: 'Speech Synthesis' },
  { key: 'DocumentIntelligence', label: 'Document Intelligence' },
  { key: 'Embeddings', label: 'Embeddings' },
];

interface ServicesTabProps {
  focusedService?: ServiceKey | null;
  onFocusedServiceHandled?: () => void;
}

export function ServicesTab({ focusedService = null, onFocusedServiceHandled }: ServicesTabProps) {
  const [activeService, setActiveService] = useState<ServiceKey>(() => focusedService ?? 'SpeechTranscription');

  useEffect(() => {
    if (!focusedService) {
      return;
    }
    setActiveService(focusedService);
    onFocusedServiceHandled?.();
  }, [focusedService, onFocusedServiceHandled]);

  return (
    <div className="space-y-6">
      <div className="rounded border border-gray-200 bg-white p-3">
        <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-600">Services</div>
        <div className="flex flex-wrap gap-2">
          {services.map((service) => (
            <button
              key={service.key}
              type="button"
              onClick={() => setActiveService(service.key)}
              className={`rounded border px-3 py-1.5 text-sm ${
                activeService === service.key
                  ? 'border-blue-500 bg-blue-50 text-blue-700'
                  : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
              }`}
            >
              {service.label}
            </button>
          ))}
        </div>
      </div>

      {activeService === 'Embeddings' ? <EmbeddingsEditor /> : null}
      {activeService === 'ImageGeneration' ? <ImageGenerationEditor /> : null}
      {activeService === 'DocumentIntelligence' ? <DocumentIntelligenceEditor /> : null}
      {activeService === 'SpeechTranscription' ? <SpeechTranscriptionEditor /> : null}
      {activeService === 'SpeechSynthesis' ? <SpeechSynthesisEditor /> : null}
    </div>
  );
}
