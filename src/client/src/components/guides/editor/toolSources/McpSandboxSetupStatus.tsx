import { FaSpinner } from 'react-icons/fa';
import { ConfirmationDialog } from '../../../common/ConfirmationDialog';
import {
  sandboxSetupStatusChipClassName,
  sandboxSetupStatusLabel,
  type McpSandboxSetupStatusKind,
} from './mcpRuntimeMode';

export interface McpSandboxSetupStatusProps {
  setupStatus: McpSandboxSetupStatusKind;
  panelState: string;
  showApplyConfirm: boolean;
  isBusy: boolean;
  onRequestApply: () => void;
  onConfirmApply: () => void;
  onCancelApply: () => void;
}

export function McpSandboxSetupStatus({
  setupStatus,
  panelState,
  showApplyConfirm,
  isBusy,
  onRequestApply,
  onConfirmApply,
  onCancelApply,
}: McpSandboxSetupStatusProps) {
  return (
    <div className="rounded-md border border-gray-200 bg-gray-50 p-3 space-y-2">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <div className="flex items-center gap-2">
          <span className="text-xs font-medium text-gray-700">Sandbox setup</span>
          <span
            className={`inline-flex px-2 py-0.5 rounded text-xs font-medium ${sandboxSetupStatusChipClassName(setupStatus)}`}
            data-testid="mcp-sandbox-setup-status"
          >
            {sandboxSetupStatusLabel(setupStatus)}
          </span>
        </div>
        <button
          type="button"
          onClick={onRequestApply}
          disabled={isBusy}
          className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-orange-700 bg-orange-50 border border-orange-200 rounded-md hover:bg-orange-100 disabled:opacity-50"
          data-testid="mcp-apply-sandbox-setup"
        >
          {panelState === 'applying' && <FaSpinner className="w-3 h-3 animate-spin" />}
          Apply sandbox packages
        </button>
      </div>
      <p className="text-xs text-gray-600">
        Saving stages package setup for this guide scope. Apply installs into the sandbox shared by every notebook
        using this guide in this project.
      </p>

      <ConfirmationDialog
        isOpen={showApplyConfirm}
        onClose={onCancelApply}
        onConfirm={onConfirmApply}
        title="Apply sandbox packages?"
        message="Applying installs packages into the sandbox shared by every notebook using this guide in this project."
        confirmText="Apply"
        cancelText="Cancel"
        confirmButtonClass="bg-teal-600 hover:bg-teal-700 text-white"
        isLoading={panelState === 'applying'}
      />
    </div>
  );
}
