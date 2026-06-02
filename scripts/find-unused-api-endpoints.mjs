#!/usr/bin/env node
/**
 * Compare swagger.json API definitions against client source usage.
 *
 * Usage:
 *   node scripts/find-unused-api-endpoints.mjs
 *   node scripts/find-unused-api-endpoints.mjs --swagger path/to/swagger.json --client src/client/src
 *   node scripts/find-unused-api-endpoints.mjs --client ../GuideAntsChat/client/src
 *   node scripts/find-unused-api-endpoints.mjs --json
 *   node scripts/find-unused-api-endpoints.mjs --output scripts/unused-api-endpoints.review.txt
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..');

const HTTP_METHODS = new Set(['get', 'post', 'put', 'patch', 'delete', 'head', 'options']);

const DEFAULT_SWAGGER = path.join(repoRoot, 'swagger.json');
const DEFAULT_CLIENT_ROOTS = [
  path.join(repoRoot, 'src', 'client', 'src'),
  path.resolve(repoRoot, '..', 'GuideAntsChat', 'client', 'src'),
];

const CLIENT_EXTENSIONS = new Set(['.ts', '.tsx', '.js', '.jsx', '.mjs']);
const CLIENT_IGNORE_DIRS = new Set(['node_modules', 'dist', 'build', '.git']);
const DEFAULT_REVIEW_OUTPUT = path.join(repoRoot, 'scripts', 'unused-api-endpoints.review.txt');

function parseArgs(argv) {
  const options = {
    swaggerPath: DEFAULT_SWAGGER,
    clientRoots: null,
    clientRootsExplicit: false,
    json: false,
    includeTests: true,
    outputPath: null,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--json') {
      options.json = true;
    } else if (arg === '--exclude-tests') {
      options.includeTests = false;
    } else if (arg === '--swagger') {
      options.swaggerPath = path.resolve(argv[++i]);
    } else if (arg === '--client') {
      if (!options.clientRootsExplicit) {
        options.clientRoots = [];
        options.clientRootsExplicit = true;
      }
      options.clientRoots.push(path.resolve(argv[++i]));
    } else if (arg === '--output') {
      const next = argv[i + 1];
      options.outputPath = next && !next.startsWith('-')
        ? path.resolve(argv[++i])
        : DEFAULT_REVIEW_OUTPUT;
    } else if (arg === '--help' || arg === '-h') {
      printHelp();
      process.exit(0);
    } else {
      console.error(`Unknown argument: ${arg}`);
      printHelp();
      process.exit(1);
    }
  }

  return options;
}

function printHelp() {
  console.log(`Find swagger endpoints not referenced by the client.

Options:
  --swagger <path>     Path to swagger.json (default: ./swagger.json)
  --client <path>      Client source root (repeatable; default: GuideAnts src/client/src
                       and sibling ../GuideAntsChat/client/src when present)
  --exclude-tests      Ignore *.test.* and __tests__ directories
  --json               Emit machine-readable JSON
  --output [path]      Write editable review list (default: scripts/unused-api-endpoints.review.txt)
  -h, --help           Show this help
`);
}

function loadSwaggerEndpoints(swaggerPath) {
  const spec = JSON.parse(fs.readFileSync(swaggerPath, 'utf8'));
  const endpoints = [];

  for (const [routePath, operations] of Object.entries(spec.paths ?? {})) {
    for (const [method, operation] of Object.entries(operations)) {
      if (!HTTP_METHODS.has(method)) {
        continue;
      }

      endpoints.push({
        method: method.toUpperCase(),
        path: routePath,
        operationId: operation.operationId ?? null,
        tags: operation.tags ?? [],
      });
    }
  }

  endpoints.sort((a, b) => a.path.localeCompare(b.path) || a.method.localeCompare(b.method));
  return endpoints;
}

function walkFiles(rootDir, includeTests) {
  const files = [];

  function walk(currentDir) {
    for (const entry of fs.readdirSync(currentDir, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (CLIENT_IGNORE_DIRS.has(entry.name)) {
          continue;
        }
        if (!includeTests && entry.name === '__tests__') {
          continue;
        }
        walk(path.join(currentDir, entry.name));
        continue;
      }

      const ext = path.extname(entry.name);
      if (!CLIENT_EXTENSIONS.has(ext)) {
        continue;
      }

      if (!includeTests && /\.(test|spec)\.[cm]?[jt]sx?$/.test(entry.name)) {
        continue;
      }

      files.push(path.join(currentDir, entry.name));
    }
  }

  walk(rootDir);
  return files;
}

function stripQuery(pathValue) {
  return pathValue.split('?')[0];
}

function normalizeApiPath(rawPath) {
  let value = stripQuery(rawPath.trim());
  if (!value.startsWith('/')) {
    return null;
  }

  if (!value.startsWith('/api/') && value !== '/api') {
    value = `/api${value}`;
  }

  value = value
    .replace(/\$\{encodeURIComponent\([^}]+\)\}/g, '*')
    .replace(/\$\{[^}]+\}/g, '*')
    .replace(/\{[^}]+\}/g, '*')
    .replace(/\/+/g, '/')
    .replace(/\/+$/, '');

  if (value === '') {
    value = '/api';
  }

  return value;
}

function looksLikeApiPath(value) {
  if (!value.startsWith('/')) {
    return false;
  }

  if (value.startsWith('/api/') || value === '/api') {
    return true;
  }

  const apiPrefixes = [
    '/projects/',
    '/settings/',
    '/guides/',
    '/assistants/',
    '/notebooks/',
    '/conversations/',
    '/invocations/',
    '/usage/',
    '/users/',
    '/catalogs/',
    '/operations/',
    '/lineage/',
    '/speech/',
    '/published/',
    '/teams/',
    '/test/',
  ];

  return apiPrefixes.some((prefix) => value.startsWith(prefix));
}

function extractPathLiterals(source) {
  const paths = new Set();
  const patterns = [
    /`([^`\\]|\\.)*`/gs,
    /'([^'\\]|\\.)*'/gs,
    /"([^"\\]|\\.)*"/gs,
  ];

  for (const pattern of patterns) {
    for (const match of source.matchAll(pattern)) {
      const literal = match[0].slice(1, -1);
      if (!literal.includes('/')) {
        continue;
      }

      const candidates = new Set([literal]);

      // Template literals often embed `${API_BASE_URL}/projects/...`.
      if (literal.includes('${')) {
        const withoutBase = literal
          .replace(/\$\{API_BASE_URL\}/g, '')
          .replace(/\$\{apiBaseUrl\}/g, '')
          .replace(/\$\{baseUrl\}/g, '');
        candidates.add(withoutBase);
      }

      for (const candidate of candidates) {
        const pathStart = candidate.search(/\/(?:api\/|[a-z])/i);
        if (pathStart === -1) {
          continue;
        }

        let fragment = candidate.slice(pathStart);
        fragment = fragment.replace(/\$\{[^}]+\}/g, '*');
        fragment = stripQuery(fragment);

        if (looksLikeApiPath(fragment.startsWith('/api') ? fragment : fragment)) {
          const normalized = normalizeApiPath(fragment.startsWith('/api') ? fragment : fragment);
          if (normalized) {
            paths.add(normalized);
          }
        }
      }
    }
  }

  return paths;
}

function patternToRegExp(normalizedPath) {
  const escaped = normalizedPath
    .split('*')
    .map((segment) => segment.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
    .join('[^/?\'"`\\s]+');

  return new RegExp(escaped, 'i');
}

function pathPatternVariants(normalizedPath) {
  const variants = [normalizedPath];

  if (normalizedPath.startsWith('/api/')) {
    variants.push(normalizedPath.slice(4));
  } else if (normalizedPath === '/api') {
    variants.push('/');
  }

  return [...new Set(variants)];
}

function pathPatternMatchesSource(normalizedPath, source) {
  for (const variant of pathPatternVariants(normalizedPath)) {
    if (patternToRegExp(variant).test(source)) {
      return variant;
    }
  }

  const core = normalizedPath.replace(/^\/api\/?/, '');
  const segments = core
    .split('*')
    .map((segment) => segment.replace(/^\/+|\/+$/g, ''))
    .filter(Boolean);

  if (segments.length === 0) {
    return null;
  }

  let searchFrom = 0;
  for (const segment of segments) {
    const foundAt = source.indexOf(segment, searchFrom);
    if (foundAt === -1) {
      return null;
    }
    searchFrom = foundAt + segment.length;
  }

  return segments.join('/');
}

function endpointPattern(endpoint) {
  return normalizeApiPath(endpoint.path);
}

function findUsage(endpoint, clientPaths, clientSource) {
  const pattern = endpointPattern(endpoint);

  const matchedClientPaths = [...clientPaths].filter((clientPath) => clientPath === pattern);

  if (matchedClientPaths.length > 0) {
    return { kind: 'literal', matches: matchedClientPaths };
  }

  const patternMatch = pathPatternMatchesSource(pattern, clientSource);
  if (patternMatch) {
    return { kind: 'pattern', matches: [patternMatch] };
  }

  return null;
}

function resolveClientRoots(options) {
  if (options.clientRootsExplicit) {
    return options.clientRoots;
  }
  return DEFAULT_CLIENT_ROOTS.filter((root) => fs.existsSync(root));
}

function formatReviewFile(unused, clientRoots) {
  const generatedAt = new Date().toISOString().slice(0, 10);
  const clientList = clientRoots.map((root) => path.relative(repoRoot, root) || root).join(', ');
  const lines = [
    '# GuideAnts API endpoints not matched in client source scan',
    `# Generated: ${generatedAt}`,
    `# Source: node scripts/find-unused-api-endpoints.mjs`,
    `# Client roots: ${clientList}`,
    '#',
    '# How to edit:',
    '#   - Delete a line (or comment with #) if it is a false positive — the client',
    '#     uses it indirectly (avatar URLs, public pages, server-side, etc.)',
    '#   - Add notes after # on each line',
    '#   - Lines left uncommented are your confirmed-unused list',
    '#',
    '# Format:',
    '#   METHOD PATH # tag [(operationId)] [notes]',
    '',
  ];

  for (const endpoint of unused) {
    const tag = endpoint.tags[0] ?? '(untagged)';
    const operationSuffix = endpoint.operationId ? ` (${endpoint.operationId})` : '';
    lines.push(`${endpoint.method} ${endpoint.path} # ${tag}${operationSuffix}`);
  }

  lines.push('');
  return `${lines.join('\n')}`;
}

function analyzeEndpoints(options, clientRoots) {
  const endpoints = loadSwaggerEndpoints(options.swaggerPath);
  const clientFiles = clientRoots.flatMap((root) => walkFiles(root, options.includeTests));
  const clientSource = clientFiles.map((file) => fs.readFileSync(file, 'utf8')).join('\n');
  const clientPaths = new Set();

  for (const file of clientFiles) {
    for (const extractedPath of extractPathLiterals(fs.readFileSync(file, 'utf8'))) {
      clientPaths.add(extractedPath);
    }
  }

  const used = [];
  const unused = [];

  for (const endpoint of endpoints) {
    const usage = findUsage(endpoint, clientPaths, clientSource);
    if (usage) {
      used.push({ ...endpoint, usage });
    } else {
      unused.push(endpoint);
    }
  }

  return { endpoints, clientFiles, used, unused };
}

function main() {
  const options = parseArgs(process.argv.slice(2));

  if (!fs.existsSync(options.swaggerPath)) {
    console.error(`Swagger file not found: ${options.swaggerPath}`);
    process.exit(1);
  }

  const clientRoots = resolveClientRoots(options);
  if (clientRoots.length === 0) {
    console.error('No client roots found. Pass --client <path> or ensure default paths exist.');
    process.exit(1);
  }

  for (const clientRoot of clientRoots) {
    if (!fs.existsSync(clientRoot)) {
      console.error(`Client root not found: ${clientRoot}`);
      process.exit(1);
    }
  }

  const { endpoints, clientFiles, used, unused } = analyzeEndpoints(options, clientRoots);

  if (options.json) {
    console.log(JSON.stringify({
      swaggerPath: options.swaggerPath,
      clientRoots,
      totalEndpoints: endpoints.length,
      usedCount: used.length,
      unusedCount: unused.length,
      unused,
      used,
    }, null, 2));
    return;
  }

  console.log('GuideAnts unused API endpoint report');
  console.log(`Swagger: ${options.swaggerPath}`);
  console.log('Client roots:');
  for (const clientRoot of clientRoots) {
    console.log(`  ${clientRoot}`);
  }
  console.log(`Scanned ${clientFiles.length} file(s)`);
  console.log(`Total endpoints: ${endpoints.length}`);
  console.log(`Used by client: ${used.length}`);
  console.log(`Unused by client: ${unused.length}`);
  console.log('');

  if (unused.length === 0) {
    console.log('All swagger endpoints appear to be referenced by the client.');
    if (options.outputPath) {
      fs.writeFileSync(options.outputPath, formatReviewFile(unused, clientRoots), 'utf8');
      console.log(`Wrote editable review list to ${options.outputPath}`);
    }
    return;
  }

  console.log('Unused endpoints:');
  console.log('');
  console.log('Note: Endpoints used only via URLs returned by the API (e.g. avatar');
  console.log('images) or external/public clients may appear here even though they');
  console.log('are exercised at runtime.');
  console.log('');

  let currentTag = null;
  for (const endpoint of unused) {
    const tag = endpoint.tags[0] ?? '(untagged)';
    if (tag !== currentTag) {
      currentTag = tag;
      console.log(`[${tag}]`);
    }

    const operationSuffix = endpoint.operationId ? ` (${endpoint.operationId})` : '';
    console.log(`  ${endpoint.method.padEnd(7)} ${endpoint.path}${operationSuffix}`);
  }

  if (options.outputPath) {
    fs.writeFileSync(options.outputPath, formatReviewFile(unused, clientRoots), 'utf8');
    console.log('');
    console.log(`Wrote editable review list to ${options.outputPath}`);
  }
}

main();
