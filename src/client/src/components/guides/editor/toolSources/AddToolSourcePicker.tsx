import { useEffect, useRef, useId } from 'react';
import { FaTimes, FaGlobe, FaDesktop, FaCode, FaWrench, FaFileCode, FaNetworkWired } from 'react-icons/fa';
import type { DraftSourceKind } from './openApiDescriptorBuilder';

export interface AddToolSourcePickerProps {
  isOpen: boolean;
  onClose: () => void;
  onSelect: (kind: DraftSourceKind) => void;
  showAdvanced?: boolean;
}

interface PickerOption {
  kind: DraftSourceKind;
  label: string;
  description: string;
  icon: React.ReactNode;
  advanced?: boolean;
}

const OPTIONS: PickerOption[] = [
  {
    kind: 'web-api',
    label: 'Web API',
    description: 'Connect to an HTTP API with server URL, auth, and operations.',
    icon: <FaGlobe className="w-5 h-5 text-indigo-600" />,
  },
  {
    kind: 'client-actions',
    label: 'Client Actions',
    description: 'Expose actions handled by the client application via client bridge.',
    icon: <FaDesktop className="w-5 h-5 text-purple-600" />,
  },
  {
    kind: 'sandbox-module',
    label: 'Sandbox Module',
    description: 'Run Python functions in the sandbox via an init module.',
    icon: <FaCode className="w-5 h-5 text-orange-600" />,
  },
  {
    kind: 'mcp-connection',
    label: 'MCP Connection',
    description: 'Connect to an MCP server, discover tools, and expose them via client bridge.',
    icon: <FaNetworkWired className="w-5 h-5 text-teal-600" />,
  },
  {
    kind: 'local-function',
    label: 'Local Function',
    description: 'Invoke a .NET static method on the server (advanced).',
    icon: <FaWrench className="w-5 h-5 text-gray-600" />,
    advanced: true,
  },
  {
    kind: 'raw-openapi',
    label: 'Raw OpenAPI',
    description: 'Start from a blank OpenAPI descriptor and edit JSON directly.',
    icon: <FaFileCode className="w-5 h-5 text-gray-600" />,
    advanced: true,
  },
];

export function AddToolSourcePicker({
  isOpen,
  onClose,
  onSelect,
  showAdvanced = true,
}: AddToolSourcePickerProps) {
  const titleId = useId();
  const firstButtonRef = useRef<HTMLButtonElement>(null);
  const triggerRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    if (isOpen) {
      triggerRef.current = document.activeElement as HTMLElement;
      const timer = setTimeout(() => firstButtonRef.current?.focus(), 50);
      return () => clearTimeout(timer);
    }
    triggerRef.current?.focus();
    return undefined;
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        onClose();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [isOpen, onClose]);

  if (!isOpen) return null;

  const visibleOptions = OPTIONS.filter((o) => showAdvanced || !o.advanced);

  return (
    <div
      className="fixed inset-0 z-50 overflow-y-auto"
      data-tour-id="guide.tools.addToolSource.picker"
      role="presentation"
    >
      <div className="flex min-h-screen items-center justify-center p-4">
        <div className="fixed inset-0 bg-black bg-opacity-50" onClick={onClose} aria-hidden="true" />

        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby={titleId}
          className="relative bg-white rounded-lg shadow-xl max-w-lg w-full"
        >
          <div className="flex items-center justify-between p-4 border-b border-gray-200">
            <h2 id={titleId} className="text-lg font-semibold text-gray-900">
              Add Tool Source
            </h2>
            <button
              type="button"
              onClick={onClose}
              className="text-gray-400 hover:text-gray-600 p-2 rounded-lg hover:bg-gray-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              aria-label="Close"
            >
              <FaTimes className="w-5 h-5" />
            </button>
          </div>

          <div className="p-4 space-y-2" role="listbox" aria-label="Tool source types">
            {visibleOptions.map((option, index) => (
              <button
                key={option.kind}
                ref={index === 0 ? firstButtonRef : undefined}
                type="button"
                role="option"
                onClick={() => onSelect(option.kind)}
                className="w-full flex items-start gap-3 p-3 text-left border border-gray-200 rounded-lg hover:border-blue-400 hover:bg-blue-50 transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                data-testid={`picker-option-${option.kind}`}
              >
                <div className="mt-0.5">{option.icon}</div>
                <div>
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium text-gray-900">{option.label}</span>
                    {option.advanced && (
                      <span className="text-xs px-1.5 py-0.5 rounded bg-gray-100 text-gray-600">
                        Advanced
                      </span>
                    )}
                  </div>
                  <p className="text-xs text-gray-600 mt-0.5">{option.description}</p>
                </div>
              </button>
            ))}
          </div>

          <div className="px-4 py-3 border-t border-gray-200 bg-gray-50 rounded-b-lg">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
            >
              Cancel
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

export type { DraftSourceKind };
