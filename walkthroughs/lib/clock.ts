import fs from 'node:fs';
import path from 'node:path';

/** Milliseconds since the walkthrough run epoch (set when the scenario starts). */
let cachedT0: number | undefined;

export function runDir(): string | undefined {
  const dir = process.env.WALKTHROUGH_RUN_DIR;
  return dir && dir.length > 0 ? dir : undefined;
}

function loadT0EpochMs(): number {
  if (cachedT0 !== undefined) {
    return cachedT0;
  }

  const dir = runDir();
  if (dir) {
    try {
      const raw = fs.readFileSync(path.join(dir, 't0.epoch'), 'utf8').trim();
      const fromFile = Number(raw);
      if (Number.isFinite(fromFile) && fromFile > 0) {
        cachedT0 = fromFile;
        return cachedT0;
      }
    } catch {
      // fall through
    }
  }

  const fromEnv = Number(process.env.WALKTHROUGH_T0_EPOCH_MS);
  cachedT0 = Number.isFinite(fromEnv) && fromEnv > 0 ? fromEnv : Date.now();
  return cachedT0;
}

export function nowMs(): number {
  return Date.now() - loadT0EpochMs();
}

export function walkthroughMode(): 'record' | 'test' {
  return process.env.WALKTHROUGH_MODE === 'test' ? 'test' : 'record';
}
