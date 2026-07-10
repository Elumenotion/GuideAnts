import type { Locator, Page } from '@playwright/test';

import type { Timeline } from './timeline.js';

const MOVE_MS = 650;

export interface TourStopOptions {
  title: string;
  subtitle?: string;
  dwellMs?: number;
}

interface TargetBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface CalloutPlacement {
  x: number;
  y: number;
  side: 'top' | 'bottom' | 'left' | 'right';
  arrowX: number;
  arrowY: number;
}

function installPointerDom(): void {
  const moveMs = 650;
  document.getElementById('pw-tutorial-overlay')?.remove();
  document.getElementById('pw-tutorial-styles')?.remove();

  const style = document.createElement('style');
  style.id = 'pw-tutorial-styles';
  style.textContent = `
    #pw-tutorial-overlay {
      position: fixed;
      inset: 0;
      z-index: 2147483647;
      pointer-events: none;
    }
    #pw-tutorial-ring {
      position: fixed;
      pointer-events: none;
      border: 3px solid #2563eb;
      border-radius: 12px;
      box-shadow: 0 0 0 5px rgba(37, 99, 235, 0.18), 0 0 20px rgba(37, 99, 235, 0.28);
      transition: left ${moveMs}ms ease-in-out, top ${moveMs}ms ease-in-out,
        width ${moveMs}ms ease-in-out, height ${moveMs}ms ease-in-out;
      opacity: 0;
    }
    #pw-tutorial-ring.visible {
      opacity: 1;
    }
    #pw-tutorial-callout {
      position: fixed;
      left: 0;
      top: 0;
      pointer-events: none;
      max-width: min(300px, calc(100vw - 48px));
      opacity: 0;
      transition: left ${moveMs}ms ease-in-out, top ${moveMs}ms ease-in-out,
        opacity 250ms ease-in-out;
    }
    #pw-tutorial-callout.visible {
      opacity: 1;
    }
    #pw-tutorial-callout .bubble {
      background: linear-gradient(180deg, #ffffff 0%, #f8fafc 100%);
      color: #0f172a;
      border: 2px solid #2563eb;
      border-radius: 14px;
      padding: 10px 14px;
      font: 600 14px/1.35 "Segoe UI", system-ui, sans-serif;
      box-shadow: 0 10px 24px rgba(37, 99, 235, 0.18);
      animation: pw-bob 1.8s ease-in-out infinite;
    }
    #pw-tutorial-callout .bubble .title {
      display: block;
    }
    #pw-tutorial-callout .bubble .subtitle {
      display: block;
      margin-top: 4px;
      font-weight: 500;
      color: #64748b;
      font-size: 12px;
    }
    #pw-tutorial-callout .arrow {
      position: absolute;
      width: 0;
      height: 0;
      pointer-events: none;
    }
    #pw-tutorial-callout.side-bottom .arrow {
      left: var(--arrow-x, 24px);
      top: -12px;
      border-left: 10px solid transparent;
      border-right: 10px solid transparent;
      border-bottom: 12px solid #2563eb;
    }
    #pw-tutorial-callout.side-top .arrow {
      left: var(--arrow-x, 24px);
      bottom: -12px;
      border-left: 10px solid transparent;
      border-right: 10px solid transparent;
      border-top: 12px solid #2563eb;
    }
    #pw-tutorial-callout.side-left .arrow {
      left: calc(100% - 1px);
      top: var(--arrow-y, 18px);
      border-top: 10px solid transparent;
      border-bottom: 10px solid transparent;
      border-left: 12px solid #2563eb;
    }
    #pw-tutorial-callout.side-right .arrow {
      right: calc(100% - 1px);
      top: var(--arrow-y, 18px);
      border-top: 10px solid transparent;
      border-bottom: 10px solid transparent;
      border-right: 12px solid #2563eb;
    }
    #pw-tutorial-callout.flash .bubble {
      border-color: #16a34a;
      box-shadow: 0 0 0 4px rgba(22, 163, 74, 0.18);
    }
    @keyframes pw-bob {
      0%, 100% { transform: translateY(0); }
      50% { transform: translateY(-3px); }
    }
  `;
  document.head.appendChild(style);

  const overlay = document.createElement('div');
  overlay.id = 'pw-tutorial-overlay';
  overlay.innerHTML = `
    <div id="pw-tutorial-ring"></div>
    <div id="pw-tutorial-callout" class="side-bottom">
      <div class="arrow"></div>
      <div class="bubble">
        <span class="title">Tutorial pointer</span>
        <small class="subtitle">Ready</small>
      </div>
    </div>
  `;
  document.body.appendChild(overlay);

  const ring = overlay.querySelector('#pw-tutorial-ring') as HTMLDivElement;
  const callout = overlay.querySelector('#pw-tutorial-callout') as HTMLDivElement;
  const titleEl = overlay.querySelector('.title') as HTMLSpanElement;
  const subtitleEl = overlay.querySelector('.subtitle') as HTMLElement;

  function clamp(value: number, min: number, max: number): number {
    return Math.min(Math.max(value, min), max);
  }

  function measureCallout(): { width: number; height: number } {
    const rect = callout.getBoundingClientRect();
    return {
      width: Math.ceil(rect.width) || 280,
      height: Math.ceil(rect.height) || 72,
    };
  }

  function placeCallout(target: TargetBox): CalloutPlacement {
    const edge = 24;
    const gap = target.y < 72 ? 28 : 18;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const { width: bubbleW, height: bubbleH } = measureCallout();
    const targetCx = target.x + target.width / 2;
    const targetCy = target.y + target.height / 2;
    const nearRight = target.x + target.width > vw * 0.68;
    const nearLeft = target.x < vw * 0.18;
    const nearTop = target.y < vh * 0.18;

    const sideOrder: Array<CalloutPlacement['side']> = nearRight
      ? ['left', 'bottom', 'top', 'right']
      : nearLeft
        ? ['right', 'bottom', 'top', 'left']
        : nearTop
          ? ['bottom', 'right', 'left', 'top']
          : ['bottom', 'top', 'right', 'left'];

    const candidates: Record<CalloutPlacement['side'], CalloutPlacement> = {
      bottom: {
        side: 'bottom',
        x: targetCx - bubbleW / 2,
        y: target.y + target.height + gap,
        arrowX: 24,
        arrowY: 18,
      },
      top: {
        side: 'top',
        x: targetCx - bubbleW / 2,
        y: target.y - bubbleH - gap,
        arrowX: 24,
        arrowY: 18,
      },
      left: {
        side: 'left',
        x: target.x - bubbleW - gap,
        y: targetCy - bubbleH / 2,
        arrowX: 24,
        arrowY: 18,
      },
      right: {
        side: 'right',
        x: target.x + target.width + gap,
        y: targetCy - bubbleH / 2,
        arrowX: 24,
        arrowY: 18,
      },
    };

    let best: CalloutPlacement | undefined;
    let bestScore = Number.POSITIVE_INFINITY;

    for (const side of sideOrder) {
      const candidate = candidates[side];
      const x = clamp(candidate.x, edge, vw - bubbleW - edge);
      const y = clamp(candidate.y, edge, vh - bubbleH - edge);
      const overflow =
        Math.abs(x - candidate.x) +
        Math.abs(y - candidate.y) +
        (x <= edge ? 120 : 0) +
        (x + bubbleW >= vw - edge ? 120 : 0) +
        (y <= edge ? 80 : 0) +
        (y + bubbleH >= vh - edge ? 80 : 0);

      if (overflow < bestScore) {
        bestScore = overflow;
        const arrowX =
          side === 'bottom' || side === 'top'
            ? clamp(targetCx - x - 10, 16, bubbleW - 28)
            : 24;
        const arrowY =
          side === 'left' || side === 'right'
            ? clamp(targetCy - y - 10, 12, bubbleH - 24)
            : 18;
        best = { ...candidate, x, y, arrowX, arrowY };
      }
    }

    return (
      best ?? {
        side: 'bottom',
        x: clamp(vw / 2 - bubbleW / 2, edge, vw - bubbleW - edge),
        y: clamp(target.y + target.height + gap, edge, vh - bubbleH - edge),
        arrowX: 24,
        arrowY: 18,
      }
    );
  }

  function applyPlacement(target: TargetBox, showRing: boolean): void {
    const pad = 10;
    if (showRing) {
      ring.style.left = `${target.x - pad}px`;
      ring.style.top = `${target.y - pad}px`;
      ring.style.width = `${target.width + pad * 2}px`;
      ring.style.height = `${target.height + pad * 2}px`;
      ring.classList.add('visible');
    } else {
      ring.classList.remove('visible');
    }

    const placement = placeCallout(target);
    callout.className = `side-${placement.side} visible`;
    callout.style.left = `${placement.x}px`;
    callout.style.top = `${placement.y}px`;
    callout.style.setProperty('--arrow-x', `${placement.arrowX}px`);
    callout.style.setProperty('--arrow-y', `${placement.arrowY}px`);
  }

  window.__tutorialPointer = {
    setLabel(main: string, sub = '') {
      titleEl.textContent = main;
      subtitleEl.textContent = sub;
    },
    flash() {
      callout.classList.add('flash');
      setTimeout(() => callout.classList.remove('flash'), 450);
    },
    pointAt(target: TargetBox, showRing = true) {
      applyPlacement(target, showRing);
    },
  };
}

export class TutorialPointer {
  private initScriptRegistered = false;

  constructor(
    private readonly page: Page,
    private readonly timeline: Timeline,
  ) {}

  async registerInitScript(): Promise<void> {
    if (this.initScriptRegistered) {
      return;
    }
    await this.page.context().addInitScript(installPointerDom);
    this.initScriptRegistered = true;
  }

  async installOnPage(): Promise<void> {
    await this.page.evaluate(installPointerDom);
  }

  async install(): Promise<void> {
    await this.registerInitScript();
    await this.installOnPage();
  }

  async ensureInstalled(): Promise<void> {
    await this.registerInitScript();
    const ready = await this.page.evaluate(
      () => Boolean(document.getElementById('pw-tutorial-overlay') && window.__tutorialPointer),
    );
    if (!ready) {
      await this.installOnPage();
    }
  }

  async remove(): Promise<void> {
    try {
      await this.page.evaluate(() => {
        document.getElementById('pw-tutorial-overlay')?.remove();
        document.getElementById('pw-tutorial-styles')?.remove();
        delete window.__tutorialPointer;
      });
    } catch {
      // Page may already be closed after a timeout.
    }
  }

  async setLabel(title: string, subtitle = ''): Promise<void> {
    await this.ensureInstalled();
    await this.page.evaluate(
      ([main, sub]) => window.__tutorialPointer?.setLabel(main, sub),
      [title, subtitle],
    );
    await this.timeline.emit({
      kind: 'pointer.label',
      title,
      subtitle,
    });
  }

  async flash(): Promise<void> {
    await this.ensureInstalled();
    await this.page.evaluate(() => window.__tutorialPointer?.flash());
  }

  async pointAtBox(
    box: TargetBox,
    options: { title?: string; subtitle?: string; showRing?: boolean; animate?: boolean } = {},
  ): Promise<void> {
    await this.ensureInstalled();
    if (options.title) {
      await this.setLabel(options.title, options.subtitle ?? '');
    }
    await this.page.evaluate(
      ({ target, showRing }) => window.__tutorialPointer?.pointAt(target, showRing),
      { target: box, showRing: options.showRing ?? true },
    );
    if (options.animate ?? false) {
      await this.page.waitForTimeout(MOVE_MS + 50);
    }
    await this.timeline.emit({
      kind: 'pointer.move',
      target_box: box,
      label: options.title,
    });
  }

  async tourStop(
    locator: Locator,
    options: TourStopOptions,
  ): Promise<void> {
    await this.ensureInstalled();
    const el = locator.first();
    await el.scrollIntoViewIfNeeded();
    await el.waitFor({ state: 'visible', timeout: 15_000 });
    const box = await el.boundingBox();
    if (!box) {
      throw new Error(`Tour target not visible: ${options.title}`);
    }

    await el.hover({ force: true });
    await this.timeline.emit({
      kind: 'ui.hover',
      target: options.title,
    });

    await this.setLabel(options.title, options.subtitle ?? '');
    await this.pointAtBox(box, { showRing: true, animate: true });
    await this.page.waitForTimeout(options.dwellMs ?? 1200);
  }
}

declare global {
  interface Window {
    __tutorialPointer?: {
      setLabel: (main: string, sub?: string) => void;
      flash: () => void;
      pointAt: (target: TargetBox, showRing?: boolean) => void;
    };
  }
}
