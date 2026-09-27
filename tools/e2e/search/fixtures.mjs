#!/usr/bin/env node

import { constants, readFileSync } from 'node:fs';
import { open, realpath } from 'node:fs/promises';
import { createServer } from 'node:http';
import { extname, isAbsolute, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const payloadExtensions = new Set(['.torrent', '.nzb', '.mp4', '.mkv', '.avi', '.mov', '.ts', '.m4v', '.webm']);

function xml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&apos;');
}

function tokens(value) {
  return String(value ?? '').toLowerCase().match(/[\p{L}\p{N}]+/gu) ?? [];
}

function matchesTitle(title, query, mode) {
  const wanted = tokens(query);
  if (wanted.length === 0) return true;
  const actual = tokens(title);
  if (mode === 'phrase') {
    return actual.some((token, index) => token === wanted[0]
      && wanted.every((wantedToken, offset) => actual[index + offset] === wantedToken));
  }
  if (mode === 'ordered') {
    let cursor = 0;
    for (const token of actual) {
      if (token === wanted[cursor]) cursor += 1;
      if (cursor === wanted.length) return true;
    }
    return false;
  }
  const available = new Set(actual);
  return wanted.every((token) => available.has(token));
}

function normalizeConfig(input) {
  if (!input || !Array.isArray(input.releases)) throw new TypeError('config.releases must be an array');
  const pageSize = Number.isInteger(input.pageSize) && input.pageSize > 0 ? input.pageSize : 100;
  const searchMode = input.searchMode && typeof input.searchMode === 'object' ? input.searchMode.type : input.searchMode;
  if (searchMode && !['and', 'phrase', 'ordered'].includes(searchMode)) {
    throw new TypeError('searchMode must be and, phrase, or ordered');
  }
  return {
    ...input,
    pageSize,
    searchMode: searchMode ?? 'and',
    sourceErrors: Array.isArray(input.sourceErrors) ? input.sourceErrors : [],
    releases: input.releases.map((release, index) => ({ ...release, _index: index })),
  };
}

function configuredError(config, attempt, url) {
  return config.sourceErrors.find((entry) => {
    if (entry.attempt !== undefined && Number(entry.attempt) !== attempt) return false;
    if (entry.path !== undefined && entry.path !== url.pathname) return false;
    if (entry.query !== undefined && entry.query !== url.searchParams.get('q')) return false;
    return true;
  });
}

function send(res, status, body = '', contentType = 'text/plain; charset=utf-8', headers = {}) {
  res.writeHead(status, { 'content-type': contentType, ...headers });
  res.end(body);
}

function capsXml(config) {
  const categories = [...new Set(config.releases.map((release) => release.category).filter((value) => value !== undefined))];
  return `<?xml version="1.0" encoding="UTF-8"?>\n<caps>\n  <server title="Sportarr search fixture" />\n  <limits max="${config.pageSize}" default="${config.pageSize}" />\n  <searching><search available="yes" supportedParams="q,cat,offset,limit,sportarrid,after,before" /><tv-search available="yes" supportedParams="q,cat,offset,limit,sportarrid,after,before" /></searching>\n  <categories>${categories.map((category) => `<category id="${xml(category)}" name="Fixture ${xml(category)}" />`).join('')}</categories>\n</caps>\n`;
}

function releaseXml(release, baseUrl, attributePrefix) {
  const downloadUrl = release.downloadPath
    ? `${baseUrl}/payload/${release.downloadPath.split('/').map(encodeURIComponent).join('/')}`
    : `${baseUrl}/download/${encodeURIComponent(release.guid)}`;
  const attrs = [
    ['category', release.category],
    ['size', release.size],
    ['seeders', release.seeders],
    ['infohash', release.infoHash],
    ['sportarrid', release.sportarrId],
  ].filter(([, value]) => value !== undefined && value !== null);
  return `<item><title>${xml(release.title)}</title><guid isPermaLink="false">${xml(release.guid)}</guid><link>${xml(downloadUrl)}</link><pubDate>${xml(new Date(release.publishDate).toUTCString())}</pubDate><enclosure url="${xml(downloadUrl)}" length="${xml(release.size ?? 0)}" type="application/octet-stream" />${attrs.map(([name, value]) => `<${attributePrefix}:attr name="${name}" value="${xml(value)}" />`).join('')}</item>`;
}

function searchXml(releases, offset, total, baseUrl, attributePrefix) {
  const namespaces = 'xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/" xmlns:torznab="http://torznab.com/schemas/2015/feed"';
  return `<?xml version="1.0" encoding="UTF-8"?>\n<rss version="2.0" ${namespaces}><channel><title>Sportarr search fixture</title><newznab:response offset="${offset}" total="${total}" />${releases.map((release) => releaseXml(release, baseUrl, attributePrefix)).join('')}</channel></rss>\n`;
}

function filteredReleases(config, params) {
  const exactId = params.get('sportarrid');
  const categories = new Set((params.get('cat') ?? '').split(',').filter(Boolean));
  const after = params.get('after') ? Date.parse(params.get('after')) : null;
  const before = params.get('before') ? Date.parse(params.get('before')) : null;
  return config.releases
    .filter((release) => exactId
      ? String(release.sportarrId) === exactId
      : matchesTitle(release.title, params.get('q'), config.searchMode))
    .filter((release) => categories.size === 0 || categories.has(String(release.category)))
    .filter((release) => after === null || Date.parse(release.publishDate) >= after)
    .filter((release) => before === null || Date.parse(release.publishDate) <= before)
    .sort((left, right) => Date.parse(right.publishDate) - Date.parse(left.publishDate) || left._index - right._index);
}

async function servePayload(config, url, req, res, ledgerEntry) {
  if (!config.payloadRoot) return send(res, 404, 'Not found');
  let relative;
  try {
    relative = decodeURIComponent(url.pathname.slice('/payload/'.length));
  } catch {
    return send(res, 400, 'Invalid payload path');
  }
  if (!relative || isAbsolute(relative) || relative.split(/[\\/]/).includes('..')) {
    return send(res, 400, 'Invalid payload path');
  }
  const allowed = config.releases.some((release) => release.downloadPath === relative)
    || (config.payloadFiles ?? []).some((file) => (typeof file === 'string' ? file : file.path) === relative);
  if (!allowed || !payloadExtensions.has(extname(relative).toLowerCase())) return send(res, 404, 'Not found');
  const root = resolve(config.payloadRoot);
  const filePath = resolve(root, relative);
  if (!filePath.startsWith(`${root}${sep}`)) return send(res, 400, 'Invalid payload path');
  let handle;
  try {
    const [realRoot, realFile] = await Promise.all([realpath(root), realpath(filePath)]);
    if (!realFile.startsWith(`${realRoot}${sep}`)) return send(res, 400, 'Invalid payload path');
    handle = await open(realFile, constants.O_RDONLY | constants.O_NOFOLLOW);
    const details = await handle.stat();
    if (!details.isFile()) return send(res, 404, 'Not found');
    const commonHeaders = { 'accept-ranges': 'bytes', 'content-type': 'application/octet-stream' };
    const range = parseRange(req.headers.range, details.size);
    if (range === false) {
      res.writeHead(416, { ...commonHeaders, 'content-range': `bytes */${details.size}` });
      return res.end();
    }
    const start = range?.start ?? 0;
    const end = range?.end ?? Math.max(0, details.size - 1);
    const contentLength = details.size === 0 ? 0 : end - start + 1;
    ledgerEntry.declaredBytes = contentLength;
    const status = range ? 206 : 200;
    const headers = { ...commonHeaders, 'content-length': contentLength };
    if (range) headers['content-range'] = `bytes ${start}-${end}/${details.size}`;
    if (req.method === 'HEAD') {
      res.writeHead(status, headers);
      return res.end();
    }
    const stream = handle.createReadStream({ autoClose: true, start, end });
    handle = undefined;
    res.once('close', () => {
      if (!res.writableFinished) stream.destroy();
    });
    stream.once('error', () => {
      if (res.headersSent) res.destroy();
      else send(res, 500, 'Payload read failed');
    });
    res.writeHead(status, headers);
    stream.pipe(res);
    if ((config.payloadStreamErrors ?? []).includes(relative)) {
      process.nextTick(() => stream.destroy(new Error('Configured payload stream error')));
    }
  } catch {
    send(res, 404, 'Not found');
  } finally {
    await handle?.close();
  }
}

function parseRange(header, length) {
  if (!header) return null;
  const match = /^bytes=(\d*)-(\d*)$/.exec(header);
  if (!match || length === 0) return false;
  if (match[1] === '') {
    const suffixLength = Number(match[2]);
    if (!Number.isSafeInteger(suffixLength) || suffixLength <= 0) return false;
    return { start: Math.max(0, length - suffixLength), end: length - 1 };
  }
  const start = Number(match[1]);
  const requestedEnd = match[2] === '' ? length - 1 : Number(match[2]);
  if (!Number.isSafeInteger(start) || !Number.isSafeInteger(requestedEnd) || start >= length || requestedEnd < start) return false;
  return { start, end: Math.min(requestedEnd, length - 1) };
}

export function createSearchBarrier({ timeoutMs = 10_000 } = {}) {
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs <= 0 || timeoutMs > 10_000) {
    throw new TypeError('timeoutMs must be an integer from 1 through 10000');
  }
  let state = 'idle';
  let resolveEntered;
  let settleWait;
  let timer;
  const entered = new Promise((resolve) => { resolveEntered = resolve; });
  const release = () => {
    if (state === 'released' || state === 'timedout') return;
    state = 'released';
    clearTimeout(timer);
    settleWait?.resolve();
  };
  const wait = () => {
    if (state !== 'idle') return state === 'pending' ? settleWait.promise : Promise.resolve();
    state = 'pending';
    resolveEntered();
    const promise = new Promise((resolve, reject) => {
      settleWait = { resolve, reject };
      timer = setTimeout(() => {
        if (state !== 'pending') return;
        state = 'timedout';
        const error = new Error(`search response barrier timed out after ${timeoutMs}ms`);
        error.code = 'SEARCH_BARRIER_TIMEOUT';
        reject(error);
      }, timeoutMs);
    });
    settleWait.promise = promise;
    return promise;
  };
  return {
    entered,
    wait,
    release,
    get pending() { return state === 'pending'; },
  };
}

export function createFixtureServer(inputConfig, { beforeSearchResponse } = {}) {
  if (beforeSearchResponse !== undefined && typeof beforeSearchResponse !== 'function') {
    throw new TypeError('beforeSearchResponse must be a function');
  }
  const config = normalizeConfig(inputConfig);
  const ledger = [];
  const payloadLedger = [];
  let attempt = 0;
  return createServer(async (req, res) => {
    try {
    let rawPath;
    try {
      rawPath = decodeURIComponent((req.url ?? '').split('?', 1)[0]);
    } catch {
      return send(res, 400, 'Invalid request path');
    }
    if (rawPath.split(/[\\/]/).includes('..')) return send(res, 400, 'Invalid request path');
    const url = new URL(req.url, `http://${req.headers.host ?? 'localhost'}`);
    if (url.pathname === '/_fixture/payload-requests' && req.method === 'GET') {
      return send(res, 200, JSON.stringify(payloadLedger), 'application/json; charset=utf-8');
    }
    if (url.pathname === '/_fixture/requests' && req.method === 'GET') {
      return send(res, 200, JSON.stringify(ledger), 'application/json; charset=utf-8');
    }
    if (url.pathname === '/_fixture/reset' && req.method === 'POST') {
      ledger.length = 0;
      payloadLedger.length = 0;
      attempt = 0;
      return send(res, 204);
    }
    if (url.pathname.startsWith('/_fixture/')) return send(res, 404, 'Not found');
    if (url.pathname.startsWith('/payload/')) {
      const entry = { method: req.method, path: url.pathname, status: null, declaredBytes: null, finished: false };
      payloadLedger.push(entry);
      res.once('finish', () => { entry.status = res.statusCode; entry.finished = true; });
      res.once('close', () => { entry.status ??= res.statusCode; });
      return await servePayload(config, url, req, res, entry);
    }
    const apiPath = url.pathname === '/api'
      || url.pathname.endsWith('/api')
      || /\/(?:newznab|torznab)$/.test(url.pathname);
    if (!apiPath) return send(res, 404, 'Not found');

    attempt += 1;
    const entry = {
      attempt,
      method: req.method,
      path: url.pathname,
      query: Object.fromEntries(url.searchParams),
      status: 0,
      releaseGuids: [],
    };
    ledger.push(entry);
    const sourceError = configuredError(config, attempt, url);
    if (sourceError) {
      entry.status = Number(sourceError.status ?? 500);
      return send(res, entry.status, sourceError.body ?? 'Configured fixture error', sourceError.contentType);
    }
    if (Number.isInteger(config.quota) && attempt > config.quota) {
      entry.status = 429;
      return send(res, 429, 'Fixture quota exhausted', 'text/plain; charset=utf-8', { 'retry-after': String(config.retryAfter ?? 60) });
    }
    if (url.searchParams.get('t') === 'caps') {
      entry.status = 200;
      return send(res, 200, capsXml(config), 'application/xml; charset=utf-8');
    }
    if (!['search', 'tvsearch'].includes(url.searchParams.get('t') ?? 'search')) {
      entry.status = 400;
      return send(res, 400, 'Unsupported fixture request');
    }
    const matches = filteredReleases(config, url.searchParams);
    const offset = Math.max(0, Number.parseInt(url.searchParams.get('offset') ?? '0', 10) || 0);
    const requestedLimit = Math.max(1, Number.parseInt(url.searchParams.get('limit') ?? String(config.pageSize), 10) || config.pageSize);
    const limit = Math.min(config.pageSize, requestedLimit);
    const page = matches.slice(offset, offset + limit);
    entry.offset = offset;
    entry.limit = limit;
    entry.total = matches.length;
    entry.releaseGuids = page.map((release) => release.guid);
    const baseUrl = `${req.socket.encrypted ? 'https' : 'http'}://${req.headers.host}`;
    const attributePrefix = url.pathname.includes('torznab') ? 'torznab' : 'newznab';
    try { await beforeSearchResponse?.(); }
    catch (error) {
      entry.status = 500;
      throw error;
    }
    entry.status = 200;
    return send(res, 200, searchXml(page, offset, matches.length, baseUrl, attributePrefix), 'application/xml; charset=utf-8');
    } catch {
      if (res.headersSent) res.destroy();
      else send(res, 500, 'Fixture request failed');
    }
  });
}

function cliArguments(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index];
    if (!name?.startsWith('--') || argv[index + 1] === undefined) throw new Error(`Missing value for ${name ?? 'argument'}`);
    values[name.slice(2)] = argv[index + 1];
  }
  if (!values.config) throw new Error('--config is required');
  return { configPath: values.config, port: Number(values.port ?? 9080), host: values.host ?? '0.0.0.0' };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = cliArguments(process.argv.slice(2));
    const config = JSON.parse(readFileSync(args.configPath, 'utf8'));
    const server = createFixtureServer(config);
    server.listen(args.port, args.host, () => process.stdout.write(`Fixture server listening on ${args.host}:${args.port}\n`));
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}
