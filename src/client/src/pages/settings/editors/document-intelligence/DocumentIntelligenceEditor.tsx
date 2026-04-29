import type { ProviderEditorStateDto } from '../../../../types/settings';
import { ServiceEditorBase } from '../common/ServiceEditorBase';

export function DocumentIntelligenceEditor() {
  return (
    <ServiceEditorBase
      serviceId="DocumentIntelligence"
      title="Document Intelligence"
      providerExtra={(provider: ProviderEditorStateDto) => (
        <div className="space-y-4 border-t border-gray-100 pt-4">
          <div>
            <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-600">Conversion throughput</div>
            {provider.providerKind === 'Cloud' ? (
              <p className="text-sm text-gray-700">
                Microsoft Foundry Document Intelligence uses the configured endpoint, API version, timeout, and retry policy for cloud extraction.
              </p>
            ) : (
              <ul className="list-disc space-y-2 pl-5 text-sm text-gray-700">
                <li>
                  <span className="font-medium">Queue concurrency:</span>{' '}
                  <code className="rounded bg-gray-100 px-1">MaxConcurrentConversions</code> limits how many documents are processed in parallel — it does not change single-document parse cost.
                </li>
                <li>
                  <span className="font-medium">Polling:</span> <code className="rounded bg-gray-100 px-1">AsyncStatusPollIntervalMs</code>{' '}
                  controls how often conversion status is polled for async local jobs.
                </li>
              </ul>
            )}
          </div>
          {provider.providerKind !== 'Cloud' ? (
            <div>
              <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-600">Docling engine controls</div>
              <p className="text-sm text-gray-700">
                OCR, PDF backend, table mode, and image export settings are typed fields above. Adjust them to trade speed vs quality for image-heavy documents without code changes.
              </p>
            </div>
          ) : null}
        </div>
      )}
    />
  );
}
