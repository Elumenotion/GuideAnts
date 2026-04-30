import React from 'react';
import { createPortal } from 'react-dom';

interface NoChatModelDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onGoToSettings: () => void;
  blockers?: string[];
}

export const NoChatModelDialog: React.FC<NoChatModelDialogProps> = ({
  isOpen,
  onClose,
  onGoToSettings,
  blockers,
}) => {
  if (!isOpen) return null;

  const markup = (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div
        className="bg-white rounded-lg shadow-xl w-full max-w-md mx-4"
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-labelledby="no-chat-model-dialog-title"
      >
        <div className="flex flex-col p-6 pb-4">
          <div className="flex items-center gap-3 mb-2">
            <div className="flex-shrink-0 w-10 h-10 rounded-full bg-amber-100 flex items-center justify-center">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" className="w-5 h-5 text-amber-600">
                <path fillRule="evenodd" d="M9.401 3.003c1.155-2 4.043-2 5.197 0l7.355 12.748c1.154 2-.29 4.5-2.599 4.5H4.645c-2.309 0-3.752-2.5-2.598-4.5L9.4 3.003zM12 8.25a.75.75 0 01.75.75v3.75a.75.75 0 01-1.5 0V9a.75.75 0 01.75-.75zm0 8.25a.75.75 0 100-1.5.75.75 0 000 1.5z" clipRule="evenodd" />
              </svg>
            </div>
            <h2 id="no-chat-model-dialog-title" className="text-lg font-semibold text-gray-900">
              No Chat Model Configured
            </h2>
          </div>
          <p className="text-sm text-gray-600 mt-2">
            There is no chat model available for this conversation. You need to configure a default
            chat model in Settings before you can send messages.
          </p>

          {blockers && blockers.length > 0 && (
            <div className="mt-3 p-3 bg-amber-50 border border-amber-200 rounded-md">
              <ul className="list-disc pl-4 text-sm text-amber-800 space-y-1">
                {blockers.map((b, i) => (
                  <li key={i}>{b}</li>
                ))}
              </ul>
            </div>
          )}
        </div>

        <div className="px-6 py-4 border-t border-gray-200 flex justify-end space-x-3">
          <button
            type="button"
            onClick={onClose}
            className="px-4 py-2 text-sm border border-gray-300 rounded-md hover:bg-gray-50"
          >
            Dismiss
          </button>
          <button
            type="button"
            onClick={onGoToSettings}
            className="px-4 py-2 text-sm rounded-md text-white bg-blue-600 hover:bg-blue-700"
          >
            Open Settings
          </button>
        </div>
      </div>
    </div>
  );

  return createPortal(markup, document.body);
};
