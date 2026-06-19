import type { HostFolderMountDisplayState } from '../../../types/hostFolderMount';
import {
  HOST_MOUNT_DISPLAY_STATE_CLASSES,
  HOST_MOUNT_DISPLAY_STATE_LABELS,
} from '../../../utils/hostMountDisplayState';

interface HostMountStateBadgeProps {
  state: HostFolderMountDisplayState;
  className?: string;
}

export function HostMountStateBadge({ state, className = '' }: HostMountStateBadgeProps) {
  return (
    <span
      className={`inline-flex items-center rounded border px-1.5 py-0.5 text-[10px] font-medium leading-none ${HOST_MOUNT_DISPLAY_STATE_CLASSES[state]} ${className}`}
      data-testid={`host-mount-state-${state}`}
      title={HOST_MOUNT_DISPLAY_STATE_LABELS[state]}
    >
      {HOST_MOUNT_DISPLAY_STATE_LABELS[state]}
    </span>
  );
}
