import { LimitNumberField } from '../toolLimits/LimitNumberField';

interface CrewMemberLimitOverrideFieldProps {
  assistantId: string;
  value?: number;
  onChange: (value: number | undefined) => void;
  onDirtyChange?: () => void;
}

export function CrewMemberLimitOverrideField({
  assistantId,
  value,
  onChange,
  onDirtyChange,
}: CrewMemberLimitOverrideFieldProps) {
  return (
    <div className="mt-2">
      <LimitNumberField
        key={assistantId}
        id={`crew-member-limit-${assistantId}`}
        label="Max tool calls per invocation"
        value={value}
        onChange={(nextValue) => {
          onChange(nextValue);
          onDirtyChange?.();
        }}
      />
      <p className="text-xs text-gray-500 mt-1">
        Blank uses this member assistant&apos;s own per-turn limit.
      </p>
    </div>
  );
}
