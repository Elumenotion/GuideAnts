interface FinishStepProps {
  readyForBasicChat: boolean;
  totalModelCount: number;
  warningItems: string[];
}

export function FinishStep({ readyForBasicChat, totalModelCount, warningItems }: FinishStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Finish setup</h3>
        <p className="mt-1 text-sm text-gray-600">
          Basic chat is ready when the core connection is configured and at least one model exists.
        </p>
      </div>

      <div className={`rounded border px-3 py-2 text-sm ${readyForBasicChat ? 'border-emerald-200 bg-emerald-50 text-emerald-900' : 'border-amber-200 bg-amber-50 text-amber-900'}`}>
        {readyForBasicChat
          ? `Ready for basic chat with ${totalModelCount} configured model${totalModelCount === 1 ? '' : 's'}.`
          : 'Basic chat requirements are not met yet.'}
      </div>

      {warningItems.length > 0 ? (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
          <div className="font-semibold">Warning: optional services are incomplete</div>
          <ul className="mt-2 list-disc space-y-1 pl-5">
            {warningItems.map((warning) => (
              <li key={warning}>{warning}</li>
            ))}
          </ul>
        </div>
      ) : (
        <div className="rounded border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-900">
          Optional Microsot Foundry services are fully configured.
        </div>
      )}
    </div>
  );
}

