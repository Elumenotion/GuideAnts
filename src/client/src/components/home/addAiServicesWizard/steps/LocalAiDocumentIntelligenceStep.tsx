import { forwardRef } from 'react';
import { LOCAL_AI_SERVICE_PROVIDER_IDS } from '../constants';
import { LocalAiServiceStepBase, type LocalAiServiceStepHandle } from './LocalAiServiceStepBase';

interface LocalAiDocumentIntelligenceStepProps {}

export const LocalAiDocumentIntelligenceStep = forwardRef<LocalAiServiceStepHandle, LocalAiDocumentIntelligenceStepProps>(
  function LocalAiDocumentIntelligenceStep(_, ref) {
    return (
      <LocalAiServiceStepBase
        ref={ref}
        serviceId="DocumentIntelligence"
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.DocumentIntelligence}
        title="Local Document Intelligence"
        description="Configure local Docling routing fields for document conversion and extraction workloads."
        behaviorContent={(
          <div className="space-y-4 border-t border-gray-100 pt-4">
            <div>
              <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-600">Conversion throughput</div>
              <ul className="list-disc space-y-2 pl-5 text-sm text-gray-700">
                <li>
                  <span className="font-medium">Queue concurrency:</span>{' '}
                  <code className="rounded bg-gray-100 px-1">MaxConcurrentConversions</code> limits how many documents are processed in parallel.
                </li>
                <li>
                  <span className="font-medium">Polling:</span>{' '}
                  <code className="rounded bg-gray-100 px-1">AsyncStatusPollIntervalMs</code> controls how often conversion status is polled for async local jobs.
                </li>
              </ul>
            </div>
            <div>
              <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-600">Docling engine controls</div>
              <p className="text-sm text-gray-700">
                OCR, PDF backend, table mode, and image export settings are typed fields above. Adjust them to trade speed vs quality for image-heavy documents without code changes.
              </p>
            </div>
          </div>
        )}
      />
    );
  }
);
