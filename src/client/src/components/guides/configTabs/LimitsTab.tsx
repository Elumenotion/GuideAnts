interface LimitsTabProps {
  maxUserMessageLength: number | undefined;
  setMaxUserMessageLength: (length: number | undefined) => void;
  maxTurns: number | undefined;
  setMaxTurns: (turns: number | undefined) => void;
  retentionPeriod: number | undefined;
  setRetentionPeriod: (period: number | undefined) => void;
  dailyChargeLimitUsd: number | undefined;
  setDailyChargeLimitUsd: (amount: number | undefined) => void;
  billingPeriodChargeLimitUsd: number | undefined;
  setBillingPeriodChargeLimitUsd: (amount: number | undefined) => void;
}

export function LimitsTab({
  maxUserMessageLength,
  setMaxUserMessageLength,
  maxTurns,
  setMaxTurns,
  retentionPeriod,
  setRetentionPeriod,
  dailyChargeLimitUsd,
  setDailyChargeLimitUsd,
  billingPeriodChargeLimitUsd,
  setBillingPeriodChargeLimitUsd
}: LimitsTabProps) {
  return (
    <div className="space-y-6">
      <h3 className="text-lg font-medium text-gray-900">Usage Limits</h3>
      
      <div className="space-y-4">
        <div>
          <label htmlFor="maxUserMessageLength" className="block text-sm font-medium text-gray-700 mb-1">
            Max User Message Length (characters)
          </label>
          <input
            type="number"
            id="maxUserMessageLength"
            min="1"
            value={maxUserMessageLength || ''}
            onChange={(e) => setMaxUserMessageLength(e.target.value ? parseInt(e.target.value) : undefined)}
            className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
            placeholder="No limit"
          />
        </div>

        <div>
          <label htmlFor="maxTurns" className="block text-sm font-medium text-gray-700 mb-1">
            Max Conversation Turns
          </label>
          <input
            type="number"
            id="maxTurns"
            min="1"
            value={maxTurns || ''}
            onChange={(e) => setMaxTurns(e.target.value ? parseInt(e.target.value) : undefined)}
            className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
            placeholder="No limit"
          />
        </div>

        <div>
          <label htmlFor="retentionPeriod" className="block text-sm font-medium text-gray-700 mb-1">
            Retention Period (days)
          </label>
          <input
            type="number"
            id="retentionPeriod"
            min="0"
            value={retentionPeriod ?? ''}
            onChange={(e) => {
              const raw = e.target.value;
              setRetentionPeriod(raw === '' ? undefined : parseInt(raw, 10));
            }}
            className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
            placeholder="No expiration"
          />
          <p className="text-xs text-gray-500 mt-1">
            Conversations older than this many days are deleted (oldest first). Leave blank for no expiration. Use 0 to delete every conversation.
          </p>
        </div>

        <div className="pt-2 border-t border-gray-200">
          <h4 className="text-sm font-medium text-gray-900 mb-2">Cost Limits (USD)</h4>
          <p className="text-xs text-gray-500 mb-3">
            These limits are enforced on the fly by summing <code>UsageEvents.ChargeUsd</code> for this published guide's notebook.
            Daily limits use UTC days; monthly limits use the UTC calendar month.
          </p>

          <div className="space-y-4">
            <div>
              <label htmlFor="dailyChargeLimitUsd" className="block text-sm font-medium text-gray-700 mb-1">
                Daily Cost Limit (UTC day)
              </label>
              <input
                type="number"
                id="dailyChargeLimitUsd"
                min="0"
                step="0.01"
                value={dailyChargeLimitUsd ?? ''}
                onChange={(e) =>
                  setDailyChargeLimitUsd(e.target.value ? Math.max(0, parseFloat(e.target.value)) : undefined)
                }
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                placeholder="No daily limit"
              />
            </div>

            <div>
              <label htmlFor="billingPeriodChargeLimitUsd" className="block text-sm font-medium text-gray-700 mb-1">
                Monthly Cost Limit (UTC month)
              </label>
              <input
                type="number"
                id="billingPeriodChargeLimitUsd"
                min="0"
                step="0.01"
                value={billingPeriodChargeLimitUsd ?? ''}
                onChange={(e) =>
                  setBillingPeriodChargeLimitUsd(e.target.value ? Math.max(0, parseFloat(e.target.value)) : undefined)
                }
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                placeholder="No monthly limit"
              />
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}






