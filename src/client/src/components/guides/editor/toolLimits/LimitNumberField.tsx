import { useState } from 'react';
import { parseOptionalPositiveInt } from './toolLimitForm';

interface LimitNumberFieldProps {
  id: string;
  label: string;
  value?: number;
  onChange: (value: number | undefined) => void;
  onErrorChange?: (hasError: boolean) => void;
}

export function LimitNumberField({
  id,
  label,
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
        aria-describedby={error ? `${id}-error` : undefined}
      />
      {error && (
        <p id={`${id}-error`} role="alert" className="text-xs text-red-600 mt-1">
          {error}
        </p>
      )}
    </div>
  );
}
