import { spawn } from 'node:child_process';
import net from 'node:net';
import path from 'node:path';
import process from 'node:process';

const cwd = process.cwd();
const isWin = process.platform === 'win32';
const viteCli = path.join(cwd, 'node_modules', 'vite', 'bin', 'vite.js');
const electronBin = path.join(cwd, 'node_modules', '.bin', isWin ? 'electron.cmd' : 'electron');

const env = {
  ...process.env,
  ELECTRON_ENABLE_LOGGING: 'true',
  NODE_ENV: 'development',
};
delete env.ELECTRON_RUN_AS_NODE;
const normalizedEnv = Object.fromEntries(
  Object.entries(env).filter(([, value]) => value != null).map(([key, value]) => [key, String(value)])
);

function tryHostPort(host, port, timeoutMs = 1000) {
  return new Promise((resolve, reject) => {
    const socket = new net.Socket();
    let settled = false;
    const done = (fn, value) => {
      if (settled) return;
      settled = true;
      socket.destroy();
      fn(value);
    };
    socket.setTimeout(timeoutMs);
    socket.once('connect', () => done(resolve));
    socket.once('error', (err) => done(reject, err));
    socket.once('timeout', () => done(reject, new Error(`Timeout connecting to ${host}:${port}`)));
    socket.connect(port, host);
  });
}

function waitForPort(
  port,
  hosts = ['127.0.0.1', '::1', 'localhost'],
  timeoutMs = 120000,
  intervalMs = 300
) {
  const start = Date.now();
  return new Promise((resolve, reject) => {
    const tryConnect = () => {
      Promise.any(hosts.map((host) => tryHostPort(host, port)))
        .then(() => resolve())
        .catch(() => {
          if (Date.now() - start >= timeoutMs) {
            reject(new Error(`Timed out waiting for ${hosts.join('|')}:${port}`));
            return;
          }
          setTimeout(tryConnect, intervalMs);
        });
    };
    tryConnect();
  });
}

let electron = null;
const vite = spawn(process.execPath, [viteCli, '--strictPort', '--port', '5173'], {
  cwd,
  env: normalizedEnv,
  stdio: 'inherit',
});

const shutdown = (code = 0) => {
  if (electron && !electron.killed) electron.kill();
  if (!vite.killed) vite.kill();
  process.exit(code);
};

process.on('SIGINT', () => shutdown(130));
process.on('SIGTERM', () => shutdown(143));

vite.on('exit', (code) => {
  if (code !== 0) shutdown(code ?? 1);
});

try {
  await waitForPort(5173);
  electron = spawn(electronBin, ['.', '--trace-warnings', '--enable-logging'], {
    cwd,
    env: normalizedEnv,
    stdio: 'inherit',
    shell: isWin,
  });
  electron.on('exit', (code) => shutdown(code ?? 0));
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  shutdown(1);
}
