let guideantsLoaded = false;

export async function loadGuideants(): Promise<void> {
  if (!guideantsLoaded) {
    await import('guideants');
    guideantsLoaded = true;
  }
}

/** Test-only reset for module-level lazy-load state. */
export function resetGuideantsLoadStateForTests(): void {
  guideantsLoaded = false;
}
