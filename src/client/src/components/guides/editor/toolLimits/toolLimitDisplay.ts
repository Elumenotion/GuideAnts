export function formatLimitDisplay(value?: number | null): string {
  if (value == null) {
    return 'Unlimited';
  }

  return String(value);
}
