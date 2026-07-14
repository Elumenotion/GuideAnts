interface ToolbarActionErrorProps {
  message: string | null;
}

export function ToolbarActionError({ message }: ToolbarActionErrorProps) {
  if (!message) {
    return null;
  }

  return (
    <div className="text-xs text-red-700" role="alert">
      {message}
    </div>
  );
}
