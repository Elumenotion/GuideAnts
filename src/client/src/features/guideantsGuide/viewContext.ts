import { useEffect } from 'react';
import type { GuideViewContextPatch } from './types';

/**
 * Module-level registry that lets routed pages publish the part of the current
 * UI state the guide should know about (project/notebook titles, selected item,
 * active conversation, counts, ...). The guide provider sits above the router
 * and cannot read page-level contexts directly, so pages push their slice here
 * and the provider merges it into the context snapshot it injects each turn.
 *
 * The published slice carries its own `route`; the provider only merges it while
 * that route is active, so context never leaks across navigations.
 */
let currentPatch: GuideViewContextPatch | null = null;

export function publishGuideViewContext(patch: GuideViewContextPatch): void {
  currentPatch = patch;
}

export function clearGuideViewContext(): void {
  currentPatch = null;
}

export function getGuideViewContext(): GuideViewContextPatch | null {
  return currentPatch;
}

/**
 * Publish the given slice while the calling component is mounted. Pass a slice
 * that includes `route: location.pathname` so the provider can scope it to the
 * active page. The slice is re-published whenever its serialized value changes.
 */
export function usePublishGuideViewContext(patch: GuideViewContextPatch): void {
  const serialized = JSON.stringify(patch);
  useEffect(() => {
    publishGuideViewContext(JSON.parse(serialized) as GuideViewContextPatch);
    return () => {
      // Only clear if our slice is still the active one; a newly mounted page
      // may have already published its own slice before we unmount.
      const active = getGuideViewContext();
      if (active && active.route === (JSON.parse(serialized) as GuideViewContextPatch).route) {
        clearGuideViewContext();
      }
    };
  }, [serialized]);
}
