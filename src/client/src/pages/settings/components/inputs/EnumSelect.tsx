interface EnumSelectProps {
  value: string;
  options: string[];
  onChange: (value: string) => void;
}

export function EnumSelect({ value, options, onChange }: EnumSelectProps) {
  return (
    <select
      value={value}
      onChange={(event) => onChange(event.target.value)}
      className="w-full rounded border border-gray-300 px-3 py-2 text-sm"
    >
      {options.map((option) => (
        <option key={option} value={option}>
          {option}
        </option>
      ))}
    </select>
  );
}
