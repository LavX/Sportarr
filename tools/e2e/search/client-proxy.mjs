import http from 'node:http';

const targets = {
  qbittorrent: 'http://sv-b0-qbit:8080',
  sabnzbd: 'http://sv-b0-sab:8080',
};
const inspectionLimit = 64 * 1024;
const requestTimeoutMs = 30_000;

function validateClientType(clientType) {
  if (!Object.hasOwn(targets, clientType)) throw new TypeError('clientType must be qbittorrent or sabnzbd');
}

function validateTarget(value, loopbackOnly) {
  let target;
  try {
    target = new URL(value);
  } catch {
    throw new TypeError('target must be an HTTP URL');
  }
  if (target.protocol !== 'http:') throw new TypeError('target must be an HTTP URL');
  if (target.username || target.password || target.pathname !== '/' || target.search || target.hash) {
    throw new TypeError('target must contain only an HTTP origin');
  }
  if (loopbackOnly && !['127.0.0.1', '[::1]', 'localhost'].includes(target.hostname)) {
    throw new TypeError('test target must be loopback');
  }
  return target;
}

function sabModeFromUrl(requestUrl) {
  const parsed = new URL(requestUrl, 'http://proxy.invalid');
  return parsed.pathname === '/api' ? parsed.searchParams.get('mode') : null;
}

function sabModeFromBody(buffer, contentType) {
  const text = buffer.toString('latin1');
  if (contentType.startsWith('application/x-www-form-urlencoded')) {
    return new URLSearchParams(text).get('mode');
  }
  if (contentType.startsWith('multipart/form-data')) {
    return text.match(/\bname=(?:"mode"|mode)(?:;[^\r\n]*)?\r\n(?:[^\r\n]+\r\n)*\r\n(addfile|addurl)\r\n/i)?.[1] ?? null;
  }
  return null;
}

function isAdd(clientType, method, path, sabMode) {
  if (clientType === 'qbittorrent') return method === 'POST' && path === '/api/v2/torrents/add';
  return path === '/api' && ['addfile', 'addurl'].includes(sabMode);
}

function classifyResult(clientType, status, body, complete) {
  if (!complete || status < 200 || status >= 300) return { accepted: false, returnedJobIds: [] };
  if (clientType === 'qbittorrent') {
    if (body.toString('utf8').trim() === 'Ok.') return { accepted: true, returnedJobIds: [] };
    try {
      const result = JSON.parse(body.toString('utf8'));
      const ids = Array.isArray(result.added_torrent_ids) ? result.added_torrent_ids.filter(id => typeof id === 'string' && /^[a-f0-9]{40,64}$/i.test(id)) : [];
      return { accepted: result.success_count > 0 && result.failure_count === 0 && result.pending_count === 0 && ids.length > 0, returnedJobIds: ids };
    } catch { return { accepted: false, returnedJobIds: [] }; }
  }
  try {
    const result = JSON.parse(body.toString('utf8'));
    const returnedJobIds = Array.isArray(result.nzo_ids)
      ? result.nzo_ids.filter((value) => typeof value === 'string' && value.length > 0)
      : [];
    return { accepted: result.status === true && returnedJobIds.length > 0, returnedJobIds };
  } catch {
    return { accepted: false, returnedJobIds: [] };
  }
}

function stripHopByHop(headers) {
  const blocked = new Set(['connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization', 'te', 'trailer', 'transfer-encoding', 'upgrade']);
  for (const token of String(headers.connection ?? '').split(',')) blocked.add(token.trim().toLowerCase());
  return Object.fromEntries(Object.entries(headers).filter(([name]) => !blocked.has(name.toLowerCase())));
}

function createProxy(clientType, target, timeoutMs = requestTimeoutMs) {
  const server = http.createServer((clientRequest, clientResponse) => {
    const startedAt = Date.now();
    const rawUrl = clientRequest.url ?? '/';
    if (!rawUrl.startsWith('/') || rawUrl.startsWith('//')) {
      clientResponse.writeHead(400).end('Invalid request target');
      return;
    }
    const parsed = new URL(rawUrl, 'http://proxy.invalid');
    const path = parsed.pathname;
    let sabMode = clientType === 'sabnzbd' ? sabModeFromUrl(rawUrl) : null;
    const requestInspection = [];
    let requestInspectionBytes = 0;
    const entry = {
      method: clientRequest.method ?? 'GET',
      path,
      kind: isAdd(clientType, clientRequest.method, path, sabMode) ? 'add' : 'other',
      responseStatus: null,
      accepted: false,
      returnedJobIds: [],
      elapsedMs: 0,
    };
    server.requests.push(entry);
    let finalized = false;
    let deadline;
    const finalize = () => {
      if (finalized) return;
      finalized = true;
      clearTimeout(deadline);
      entry.elapsedMs = Date.now() - startedAt;
    };
    let requestEnded = false;
    let responseResult;
    const updateKind = () => {
      if (clientType === 'sabnzbd' && sabMode === null) {
        sabMode = sabModeFromBody(Buffer.concat(requestInspection), clientRequest.headers['content-type'] ?? '');
      }
      entry.kind = isAdd(clientType, clientRequest.method, path, sabMode) ? 'add' : 'other';
    };
    const classifyWhenReady = () => {
      if (!responseResult || (clientType === 'sabnzbd' && sabMode === null && !requestEnded)) return;
      updateKind();
      if (entry.kind === 'add') {
        const result = classifyResult(clientType, entry.responseStatus, responseResult.body, responseResult.complete);
        entry.accepted = result.accepted;
        entry.returnedJobIds = result.returnedJobIds;
      }
      finalize();
    };

    clientRequest.on('data', (chunk) => {
      if (requestInspectionBytes >= inspectionLimit) return;
      const retained = chunk.subarray(0, inspectionLimit - requestInspectionBytes);
      requestInspection.push(retained);
      requestInspectionBytes += retained.length;
    });
    clientRequest.on('end', () => {
      requestEnded = true;
      updateKind();
      classifyWhenReady();
    });

    const headers = { ...stripHopByHop(clientRequest.headers), host: target.host };
    const upstreamRequest = http.request({
      protocol: target.protocol,
      hostname: target.hostname,
      port: target.port,
      method: clientRequest.method,
      path: rawUrl,
      headers,
    }, (upstreamResponse) => {
      entry.responseStatus = upstreamResponse.statusCode ?? null;
      clientResponse.writeHead(upstreamResponse.statusCode ?? 502, stripHopByHop(upstreamResponse.headers));
      const responseInspection = [];
      let responseInspectionBytes = 0;
      let responseComplete = true;
      upstreamResponse.on('data', (chunk) => {
        if (responseInspectionBytes >= inspectionLimit) {
          responseComplete = false;
          return;
        }
        const retained = chunk.subarray(0, inspectionLimit - responseInspectionBytes);
        responseInspection.push(retained);
        responseInspectionBytes += retained.length;
        if (retained.length !== chunk.length) responseComplete = false;
      });
      upstreamResponse.on('end', () => {
        responseResult = { body: Buffer.concat(responseInspection), complete: responseComplete };
        classifyWhenReady();
      });
      upstreamResponse.on('error', () => {
        updateKind();
        entry.accepted = false;
        clientResponse.destroy();
        finalize();
      });
      upstreamResponse.on('aborted', () => {
        updateKind();
        entry.accepted = false;
        clientResponse.destroy();
        finalize();
      });
      upstreamResponse.pipe(clientResponse);
    });

    deadline = setTimeout(() => {
      updateKind();
      entry.accepted = false;
      upstreamRequest.destroy(new Error('Upstream request timed out'));
      if (!clientResponse.headersSent) {
        entry.responseStatus = 504;
        clientResponse.writeHead(504).end('Upstream request timed out');
      } else clientResponse.destroy();
      finalize();
    }, timeoutMs);
    upstreamRequest.on('error', () => {
      updateKind();
      entry.accepted = false;
      if (!clientResponse.headersSent) {
        entry.responseStatus = 502;
        clientResponse.writeHead(502).end('Upstream request failed');
      } else clientResponse.destroy();
      finalize();
    });
    clientRequest.on('aborted', () => {
      upstreamRequest.destroy();
      finalize();
    });
    clientRequest.on('error', () => {
      upstreamRequest.destroy();
      finalize();
    });
    clientResponse.on('error', () => {
      upstreamRequest.destroy();
      finalize();
    });
    clientResponse.on('close', () => {
      if (!clientResponse.writableEnded) {
        upstreamRequest.destroy();
        finalize();
      }
    });
    clientRequest.pipe(upstreamRequest);
  });
  server.requests = [];
  server.target = target;
  return server;
}

export function createClientProxy(options) {
  if (!options || Object.keys(options).some((key) => key !== 'clientType')) throw new TypeError('target overrides are not allowed');
  validateClientType(options.clientType);
  return createProxy(options.clientType, validateTarget(targets[options.clientType], false));
}

export function createClientProxyForTest({ clientType, target, timeoutMs = requestTimeoutMs }) {
  validateClientType(clientType);
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs <= 0 || timeoutMs > requestTimeoutMs) throw new TypeError('timeoutMs is invalid');
  return createProxy(clientType, validateTarget(target, true), timeoutMs);
}
