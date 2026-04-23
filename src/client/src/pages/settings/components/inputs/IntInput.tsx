interface IntInputProps {
  value: number | '';
  onChange: (value: number | '') => void;
  min?: number;
  max?: number;
}

export function IntInput({ value, onChange, min, max }: IntInputProps) {
  return (
    <input
      type="number"
      value={value}
      min={min}
      max={max}
      onChange={(event) => {
        const next = event.target.value;
        if (next === '') {
          onChange('');
          return;
        }
        onChange(Number.parseInt(next, 10));
      }}
      className="w-full rounded border border-gray-300 px-3 py-2 text-sm"
    />
  );
}
