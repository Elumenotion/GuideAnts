export function broadcastAuthExpired(_reason?: string): void {}

export function onAuthExpired(_callback: () => void): () => void {
  return () => {};
}
