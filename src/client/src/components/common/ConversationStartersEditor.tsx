interface ConversationStartersEditorProps {
  starters: string[];
  onChange: (starters: string[]) => void;
  label?: string;
}

export function ConversationStartersEditor({ starters, onChange, label }: ConversationStartersEditorProps) {
  const handleAdd = () => {
    onChange([...starters, '']);
  };

  const handleRemove = (index: number) => {
    onChange(starters.filter((_, i) => i !== index));
  };

  const handleChange = (index: number, value: string) => {
    const newStarters = [...starters];
    newStarters[index] = value;
    onChange(newStarters);
  };

  const handleMoveUp = (index: number) => {
    if (index === 0) return;
    const newStarters = [...starters];
    [newStarters[index - 1], newStarters[index]] = [newStarters[index], newStarters[index - 1]];
    onChange(newStarters);
  };

  const handleMoveDown = (index: number) => {
    if (index === starters.length - 1) return;
    const newStarters = [...starters];
    [newStarters[index], newStarters[index + 1]] = [newStarters[index + 1], newStarters[index]];
    onChange(newStarters);
  };

  return (
    <div className="space-y-2">
      {label && <label className="block text-sm font-medium text-gray-700">{label}</label>}
      
      <div className="space-y-2">
        {starters.map((starter, index) => (
          <div key={index} className="flex gap-2 items-start">
            {/* Move buttons */}
            <div className="flex flex-col gap-1 pt-2">
              <button
                type="button"
                onClick={() => handleMoveUp(index)}
                disabled={index === 0}
                className="p-1 text-gray-400 hover:text-gray-600 disabled:opacity-30 disabled:cursor-not-allowed"
                title="Move up"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 15l7-7 7 7" />
                </svg>
              </button>
              <button
                type="button"
                onClick={() => handleMoveDown(index)}
                disabled={index === starters.length - 1}
                className="p-1 text-gray-400 hover:text-gray-600 disabled:opacity-30 disabled:cursor-not-allowed"
                title="Move down"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                </svg>
              </button>
            </div>

            {/* Input */}
            <input
              type="text"
              value={starter}
              onChange={(e) => handleChange(index, e.target.value)}
              placeholder="Enter a conversation starter..."
              className="flex-1 px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />

            {/* Remove button */}
            <button
              type="button"
              onClick={() => handleRemove(index)}
              className="px-3 py-2 text-red-600 hover:text-red-700 hover:bg-red-50 rounded-md"
              title="Remove"
            >
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
              </svg>
            </button>
          </div>
        ))}
      </div>

      <button
        type="button"
        onClick={handleAdd}
        className="flex items-center gap-2 px-3 py-2 text-sm text-blue-600 hover:text-blue-700 hover:bg-blue-50 rounded-md"
      >
        <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
        </svg>
        Add Starter
      </button>

      {starters.length > 0 && (
        <div className="mt-4 space-y-2">
          <p className="text-sm font-medium text-gray-700">Preview</p>
          <div className="space-y-2">
            {starters.filter(s => s.trim()).map((starter, index) => (
              <div key={index} className="bg-blue-50 border border-blue-200 rounded-lg px-3 py-2 text-sm text-blue-900">
                {starter}
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}


