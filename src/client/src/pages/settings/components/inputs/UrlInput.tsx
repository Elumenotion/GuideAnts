interface UrlInputProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
}

export function UrlInput({ value, onChange, placeholder }: UrlInputProps) {
  return (
    <input
      type="url"
      value={value}
      onChange={(event) => onChange(event.target.value)}
      className="w-full rounded border border-gray-300 px-3 py-2 text-sm"
      placeholder={placeholder}
    />
  );
}
