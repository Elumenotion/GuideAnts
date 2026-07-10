import fs from 'node:fs';
import path from 'node:path';

import type { TestInfo } from '@playwright/test';

import { nowMs, runDir } from './clock.js';

export type TimelineEventKind =
  | 'scenario.start'
  | 'scenario.end'
  | 'pointer.move'
  | 'pointer.label'
  | 'ui.hover'
  | 'ui.click'
  | 'ui.fill'
  | 'typing.start'
  | 'typing.char'
  | 'typing.end'
  | 'idle.start'
  | 'idle.end'
  | 'dom.mutation'
  | 'navigate'
  | 'assert.pass'
  | 'assert.fail'
  | 'note';

export interface TimelineEvent {
  t_ms: number;
  kind: TimelineEventKind;
  [key: string]: unknown;
}

export interface TimelineSegment {
  kind: 'active' | 'idle';
  t_start_ms: number;
  t_end_ms: number;
  reason?: string;
  planned?: boolean;
  tags?: string[];
}

export class Timeline {
  private readonly events: TimelineEvent[] = [];
  private readonly eventsPath: string | undefined;
  private readonly scenarioId: string;
  private readonly scenarioVersion: string;

  constructor(testInfo: TestInfo) {
    this.scenarioId = relativeScenarioPath(testInfo);
    this.scenarioVersion =
      process.env.WALKTHROUGH_SCENARIO_VERSION ?? '0.1.0';

    const dir = runDir();
    if (dir) {
      fs.mkdirSync(dir, { recursive: true });
      this.eventsPath = path.join(dir, 'events.jsonl');
    }
  }

  async emit(
    event: { kind: TimelineEventKind; t_ms?: number } & Record<string, unknown>,
  ): Promise<void> {
    const { kind, t_ms, ...rest } = event;
    const row: TimelineEvent = {
      t_ms: t_ms ?? nowMs(),
      kind,
      ...rest,
    };
    this.events.push(row);
    if (this.eventsPath) {
      fs.appendFileSync(this.eventsPath, `${JSON.stringify(row)}\n`, 'utf8');
    }
  }

  getEvents(): readonly TimelineEvent[] {
    return this.events;
  }

  async startScenario(extra: Record<string, unknown> = {}): Promise<void> {
    const dir = runDir();
    if (dir) {
      const t0Path = path.join(dir, 't0.epoch');
      if (!fs.existsSync(t0Path)) {
        fs.writeFileSync(t0Path, `${Date.now()}\n`, 'utf8');
      }
    }
    await this.emit({
      kind: 'scenario.start',
      scenario_id: this.scenarioId,
      scenario_version: this.scenarioVersion,
      ...extra,
    });
  }

  async endScenario(status: 'pass' | 'fail', extra: Record<string, unknown> = {}): Promise<void> {
    await this.emit({
      kind: 'scenario.end',
      status,
      scenario_id: this.scenarioId,
      ...extra,
    });
  }

  writeManifest(extra: Record<string, unknown> = {}): void {
    const dir = runDir();
    if (!dir) {
      return;
    }
    const manifest = {
      schema_version: 1,
      scenario: {
        id: this.scenarioId,
        version: this.scenarioVersion,
      },
      clock: {
        t0_epoch_ms: Number(process.env.WALKTHROUGH_T0_EPOCH_MS ?? 0),
        fps: Number(process.env.WALKTHROUGH_FPS ?? 30),
      },
      events: this.events,
      ...extra,
    };
    fs.writeFileSync(
      path.join(dir, 'playwright-manifest.json'),
      `${JSON.stringify(manifest, null, 2)}\n`,
      'utf8',
    );
  }
}

function relativeScenarioPath(testInfo: TestInfo): string {
  const file = testInfo.file;
  const marker = `${path.sep}scenarios${path.sep}`;
  const idx = file.indexOf(marker);
  if (idx === -1) {
    return path.basename(file, path.extname(file));
  }
  return file
    .slice(idx + marker.length)
    .replace(/\\/g, '/')
    .replace(/\.spec\.ts$/, '');
}
