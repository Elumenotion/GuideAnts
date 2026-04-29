import { useEffect, useState } from 'react';
import { SettingsModal } from '../../pages/settings/components/shared/SettingsModal';

interface AddAiServicesWizardPlaceholderProps {
  isOpen: boolean;
  onDismiss: (persistDismissal: boolean) => void;
  onOpenSettings: (persistDismissal: boolean) => void;
}

export default function AddAiServicesWizardPlaceholder({
  isOpen,
  onDismiss,
  onOpenSettings,
}: AddAiServicesWizardPlaceholderProps) {
  const [dontAutoOpenAgain, setDontAutoOpenAgain] = useState(false);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    setDontAutoOpenAgain(false);
  }, [isOpen]);

  const dismiss = () => onDismiss(dontAutoOpenAgain);
  const openSettings = () => onOpenSettings(dontAutoOpenAgain);

  return (
    <SettingsModal
      isOpen={isOpen}
      title="Add AI Services Wizard (Preview)"
      onClose={dismiss}
      maxWidthClass="max-w-xl"
      footer={(
        <>
          <button
            type="button"
            onClick={dismiss}
            className="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50"
          >
            Not now
          </button>
          <button
            type="button"
            onClick={openSettings}
            className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
          >
            Configure in Settings
          </button>
        </>
      )}
    >
      <div className="space-y-3 text-sm text-gray-700">
        <p>
          GuideAnts detected that AI services are not fully configured yet.
          This is a placeholder for the upcoming guided setup wizard.
        </p>
        <p>
          For now, continue configuration manually in
          {' '}
          <span className="font-medium">Settings</span>
          {' '}
          using Connections, Models &amp; Runtime, and Services.
        </p>
        <label className="inline-flex items-center gap-2">
          <input
            type="checkbox"
            className="h-4 w-4 rounded border-gray-300 text-blue-600"
            checked={dontAutoOpenAgain}
            onChange={(event) => setDontAutoOpenAgain(event.target.checked)}
          />
          <span>Don&apos;t auto-open this again on this device</span>
        </label>
      </div>
    </SettingsModal>
  );
}

