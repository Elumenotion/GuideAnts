interface SecretInputProps {
  value: string;
  onChange: (value: string) => void;
  /** When true, a secret is already stored server-side (value is not echoed). */
  storedHasValue?: boolean;
  placeholder?: string;
}

const STORED_PLACEHOLDER = 'Stored — leave blank to keep; type to replace';

export function SecretInput({ value, onChange, storedHasValue = false, placeholder }: SecretInputProps) {
  const showStoredHint = storedHasValue && value === '';
  const effectivePlaceholder = showStoredHint ? STORED_PLACEHOLDER : placeholder;

  return (
    <div className="space-y-1">
      <input
        type="password"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="w-full rounded border border-gray-300 px-3 py-2 text-sm"
        placeholder={effectivePlaceholder}
        autoComplete="new-password"
        aria-label={showStoredHint ? 'API key or secret credential' : undefined}
      />
      {showStoredHint ? (
        <p className="text-xs text-gray-600">A credential is already saved for this field.</p>
      ) : null}
    </div>
  );
}
