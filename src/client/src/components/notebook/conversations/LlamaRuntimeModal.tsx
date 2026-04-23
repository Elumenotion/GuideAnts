import React from 'react';
import LoadingSpinner from '../../LoadingSpinner';
import { createPortal } from 'react-dom';

interface LlamaRuntimeModalProps {
  isOpen: boolean;
  onClose: () => void;
  status: any;
  onStartLoad: () => void;
  isPolling: boolean;
}

export const LlamaRuntimeModal: React.FC<LlamaRuntimeModalProps> = ({
  isOpen,
  onClose,
  status,
  onStartLoad,
  isPolling
}) => {
  if (!isOpen || !status) return null;

  const isFailed = status.state === 'failed';
  const isInvalid = status.state === 'invalid';

  const dialogMarkup = (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div 
        className="bg-white rounded-lg shadow-xl w-full max-w-md mx-4"
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
      >
        <div className="flex flex-col p-6 pb-4">
          <h2 className="text-lg font-semibold text-gray-900">
            {isPolling ? 'Loading Local Models...' : 
             isFailed ? 'Load Failed' : 
             isInvalid ? 'Incompatible Models' : 
             'Local Models Required'}
          </h2>
          <p className="text-sm text-gray-600 mt-2">
            {isPolling && 'Please wait while the required models are loaded into the local runtime.'}
            {!isPolling && !isFailed && !isInvalid && 'The selected assistant requires local models that are not currently loaded.'}
          </p>
        </div>

        <div className="px-6 pb-6">
          {isPolling ? (
            <div className="flex flex-col items-center justify-center space-y-4 py-8">
              <LoadingSpinner />
              <p className="text-sm text-slate-500">
                {status.activeOperation?.state === 'unloading' && 'Unloading current models...'}
                {status.activeOperation?.state === 'loading' && 'Loading new models into VRAM...'}
                {status.activeOperation?.state === 'verifying' && 'Verifying model readiness...'}
                {status.activeOperation?.state === 'queued' && 'Waiting to start...'}
              </p>
            </div>
          ) : isInvalid ? (
            <div className="space-y-4">
              <div className="p-4 bg-red-50 text-red-700 rounded-md text-sm">
                <p className="font-semibold mb-2">Cannot load models due to conflicts:</p>
                <ul className="list-disc pl-5 space-y-1">
                  {status.conflicts?.map((c: string, i: number) => (
                    <li key={i}>{c}</li>
                  ))}
                </ul>
              </div>
            </div>
          ) : (
            <div className="space-y-4">
              {isFailed && (
                <div className="p-4 bg-red-50 text-red-700 rounded-md text-sm mb-4">
                  <p className="font-semibold">Operation failed:</p>
                  <p>{status.activeOperation?.errorDetails || status.conflicts?.[0] || 'Unknown error'}</p>
                </div>
              )}

              {status.requiredModels && status.requiredModels.length > 0 && (
                <div>
                  <h4 className="text-sm font-medium text-slate-900 mb-2">Models to load:</h4>
                  <ul className="list-disc pl-5 text-sm text-slate-600">
                    {status.requiredModels.map((m: any) => (
                      <li key={m.modelId}>{m.displayName}</li>
                    ))}
                  </ul>
                </div>
              )}

              {status.loadedModels && status.loadedModels.length > 0 && (
                <div className="mt-4">
                  <h4 className="text-sm font-medium text-slate-900 mb-2">Models to unload:</h4>
                  <ul className="list-disc pl-5 text-sm text-slate-600">
                    {status.loadedModels.map((m: any) => (
                      <li key={m.modelId}>{m.displayName}</li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          )}
        </div>

        <div className="px-6 py-4 border-t border-gray-200 flex justify-end space-x-3">
          {!isPolling && (
            <>
              <button
                onClick={onClose}
                className="px-4 py-2 text-sm border border-gray-300 rounded-md hover:bg-gray-50"
              >
                Cancel
              </button>
              {!isInvalid && (
                <button
                  onClick={onStartLoad}
                  className={`px-4 py-2 text-sm rounded-md text-white ${isFailed ? 'bg-gray-600 hover:bg-gray-700' : 'bg-blue-600 hover:bg-blue-700'}`}
                >
                  {isFailed ? 'Retry Load' : 'Load Models'}
                </button>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );

  return createPortal(dialogMarkup, document.body);
};