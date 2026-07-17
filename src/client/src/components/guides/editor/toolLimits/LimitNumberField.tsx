import { useState } from 'react';
import { parseOptionalPositiveInt } from './toolLimitForm';

interface LimitNumberFieldProps {
  id: string;
  label: string;
  description?: string;
  value?: number;
  onChange: (value: number | undefined) => void;
  onErrorChange?: (hasError: boolean) => void;
}

export function LimitNumberField({
  id,
  label,
  description,
  value,
  onChange,
  onErrorChange,
}: LimitNumberFieldProps) {
  const [isFocused, setIsFocused] = useState(false);
  const [rawValue, setRawValue] = useState('');
  const displayValue = isFocused
    ? rawValue
    : value === undefined
      ? ''
      : String(value);
  const parsed = parseOptionalPositiveInt(displayValue);
  const error = parsed.ok ? undefined : parsed.error;
  const descriptionId = description ? `${id}-description` : undefined;
  const errorId = error ? `${id}-error` : undefined;
  const describedBy = [descriptionId, errorId].filter(Boolean).join(' ') || undefined;

  const handleChange = (nextRaw: string) => {
    setRawValue(nextRaw);
    const result = parseOptionalPositiveInt(nextRaw);
    onErrorChange?.(!result.ok);
    if (result.ok) {
      onChange(result.value);
    }
  };

  return (
    <div>
      <label htmlFor={id} className="block text-sm font-medium text-gray-700 mb-1">
        {label}
      </label>
      {description && (
        <p id={descriptionId} className="text-xs text-gray-500 mb-2">
          {description}
        </p>
      )}
      <input
        type="number"
        id={id}
        min={1}
        value={displayValue}
        onFocus={() => {
          setIsFocused(true);
          setRawValue(value === undefined ? '' : String(value));
        }}
        onBlur={() => {
          setIsFocused(false);
          setRawValue('');
        }}
        onChange={(event) => handleChange(event.target.value)}
        className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
        placeholder="No limit"
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
      />
      {error && (
        <p id={errorId} role="alert" className="text-xs text-red-600 mt-1">
          {error}
        </p>
      )}
    </div>
  );
}
