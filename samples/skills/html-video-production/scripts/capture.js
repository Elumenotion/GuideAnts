#!/usr/bin/env node
/* frame-by-frame capture of pure f(T) HTML animations.
 * plan.json: {fps, viewport:[w,h], hide:[ids], wait_ms, status, scenarios:[{name,html,t0,t1,out}]}
 * Resume: existing frame files (size>0) are skipped. Status file written throughout.
 */
const fs = require('fs');
const path = require('path');

function loadPlaywright() {
  try { return require('playwright'); } catch (e) {}
  for (const p of [process.cwd(), path.resolve(process.cwd(), '..')]) {
    try { return require(path.join(p, 'node_modules', 'playwright')); } catch (e) {}
  }
  console.error(JSON.stringify({ ok: false, error: 'playwright not resolvable from CWD or CWD/..' }));
  process.exit(2);
}

const args = process.argv.slice(2);
const planIdx = args.indexOf('--plan');
if (planIdx < 0) { console.error('usage: capture.js --plan plan.json [--test N]'); process.exit(2); }
const plan = JSON.parse(fs.readFileSync(args[planIdx + 1], 'utf8'));
const testIdx = args.indexOf('--test');
const testN = testIdx >= 0 ? parseInt(args[testIdx + 1], 10) : 0;

const FPS = plan.fps || 24;
const [W, H] = plan.viewport || [1280, 720];
const HIDE = plan.hide || [];
const WAIT = plan.wait_ms == null ? 800 : plan.wait_ms;
const STATUS = plan.status || 'capture_status.json';

const totalFrames = plan.scenarios.reduce((a, s) => a + Math.round((s.t1 - s.t0) * FPS), 0);
const st0 = Date.now();
const st = (o) => fs.writeFileSync(STATUS, JSON.stringify(Object.assign({
  phase: o.phase, done: o.done, total: o.total,
  rate_fps: o.rate, eta_s: o.eta == null ? 0 : Math.round(o.eta),
  elapsed_s: Math.round((Date.now() - st0) / 100) / 10,
}, o.extra || {})));

(async () => {
  const { chromium } = loadPlaywright();
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: W, height: H } });

  const jobs = plan.scenarios.map((s, k) => Object.assign({}, s,
    { n: Math.round((s.t1 - s.t0) * FPS), first: k === 0 }));
  const jobsToRun = testN > 0 ? [Object.assign({}, jobs[0], { n: testN, out: 'speedtest' })] : jobs;

  for (const job of jobsToRun) {
    fs.mkdirSync(job.out, { recursive: true });
    await page.goto('file://' + path.resolve(job.html) + '?t=' + job.t0);
    await page.waitForTimeout(WAIT);
    await page.evaluate((ids) => {
      for (const id of ids) { const el = document.getElementById(id); if (el) el.style.display = 'none'; }
    }, HIDE);
    st({ phase: testN > 0 ? 'speedtest' : job.out, done: 0, total: job.n });
    let done = 0;
    for (let i = 0; i < job.n; i++) {
      const p = path.join(job.out, 'f_' + String(i).padStart(5, '0') + '.png');
      if (fs.existsSync(p) && fs.statSync(p).size > 0) { done = i + 1; continue; } // resume
      const t = job.t0 + i / FPS;
      await page.evaluate((tt) => { T = tt; render(); }, t);
      await page.screenshot({ path: p });
      done = i + 1;
      if (i % 48 === 0) st({ phase: testN > 0 ? 'speedtest' : job.out, done, total: job.n,
        rate: done / ((Date.now() - st0) / 1000), eta: (totalFrames - done) / (done / ((Date.now() - st0) / 1000)) });
    }
    st({ phase: testN > 0 ? 'speedtest' : job.out, done, total: job.n });
  }
  await browser.close();
  const secs = (Date.now() - st0) / 1000;
  if (testN > 0) {
    console.log(JSON.stringify({ ok: true, frames: testN, seconds: Math.round(secs * 10) / 10,
      rate_fps: Math.round(testN / secs * 10) / 10, total_frames: totalFrames,
      eta_s: Math.round(totalFrames / (testN / secs)) }));
  } else {
    st({ phase: 'complete', done: totalFrames, total: totalFrames, eta: 0 });
    console.log(JSON.stringify({ ok: true, frames: totalFrames, seconds: Math.round(secs) }));
  }
})().catch((e) => { st({ phase: 'error', extra: { error: String(e) } }); console.error(String(e)); process.exit(1); });
