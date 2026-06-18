import { useEffect, useState } from 'react';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';

interface HostMountCommandDialogProps {
  isOpen: boolean;
  title: string;
  description: string;
  command: string;
  onClose: () => void;
}

export function HostMountCommandDialog({
  isOpen,
  title,
  description,
  command,
  onClose,
}: HostMountCommandDialogProps) {
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!isOpen) {
      setCopied(false);
    }
  }, [isOpen]);

  const handleCopy = async () => {
    if (!command) {
      return;
    }
    await navigator.clipboard.writeText(command);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <ConfirmationDialog
      isOpen={isOpen}
      onClose={onClose}
      onConfirm={onClose}
      title={title}
      message={description}
      body={(
        <div className="space-y-2">
          <label className="block text-xs font-medium text-gray-700">Host command</label>
          <textarea
            readOnly
            value={command}
            rows={7}
            className="w-full rounded border border-gray-300 bg-gray-50 px-3 py-2 font-mono text-xs text-gray-800"
            data-testid="host-mount-command-text"
          />
          <button
            type="button"
            onClick={() => void handleCopy()}
            className="px-3 py-1.5 text-xs font-medium text-white bg-indigo-600 rounded hover:bg-indigo-700"
            data-testid="host-mount-command-copy"
          >
            {copied ? 'Copied' : 'Copy command'}
          </button>
        </div>
      )}
      confirmText="Close"
      showCancelButton={false}
      confirmButtonClass="bg-gray-700 hover:bg-gray-800 text-white"
    />
  );
}
