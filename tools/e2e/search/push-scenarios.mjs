const fixtureOrigin = 'http://sv-b0-fixture:9080';
const clientProtocols = Object.freeze({ qbittorrent: 'torrent', sabnzbd: 'usenet' });

function validateInput({ eventId, leagueId, release, clientType }) {
  if (!Number.isSafeInteger(eventId) || eventId <= 0) throw new TypeError('eventId must be a positive integer');
  if (!Number.isSafeInteger(leagueId) || leagueId <= 0) throw new TypeError('leagueId must be a positive integer');
  if (!Object.hasOwn(clientProtocols, clientType)) throw new TypeError('clientType must be qbittorrent or sabnzbd');
  if (!release || typeof release !== 'object') throw new TypeError('release is required');
  for (const field of ['title', 'guid', 'downloadPath']) {
    if (typeof release[field] !== 'string' || release[field].length === 0) throw new TypeError(`release.${field} is required`);
  }
  if (!Number.isSafeInteger(release.size) || release.size <= 0) throw new TypeError('release.size must be a positive integer');
  if (typeof release.publishDate !== 'string' || Number.isNaN(Date.parse(release.publishDate))) {
    throw new TypeError('release.publishDate must be an ISO date');
  }
  const downloadUrl = new URL(release.downloadPath);
  if (downloadUrl.origin !== fixtureOrigin || downloadUrl.username || downloadUrl.password) {
    throw new TypeError(`release.downloadPath must use the isolated fixture at ${fixtureOrigin}`);
  }
  if (clientType === 'qbittorrent' && !/^[a-fA-F0-9]{40}$/.test(release.infoHash ?? '')) {
    throw new TypeError('release.infoHash must be a 40-character hexadecimal torrent hash');
  }
}

function expected(input, sourceSearchRequests) {
  return {
    eventId: input.eventId,
    leagueId: input.leagueId,
    fixtureReleaseGuid: input.release.guid,
    sourceSearchRequests,
    payloadDownloadRequired: true,
    allowedOutcomes: sourceSearchRequests === 0
      ? ['accepted', 'delayed', 'rejected']
      : ['accepted', 'rejected'],
  };
}

function pushBody({ release, clientType }) {
  const body = {
    title: release.title,
    downloadUrl: release.downloadPath,
    indexer: 'search-validation-fixture',
    downloadProtocol: clientProtocols[clientType],
    size: release.size,
    publishDate: release.publishDate,
  };
  if (clientType === 'qbittorrent') body.magnetUrl = `magnet:?xt=urn:btih:${release.infoHash}`;
  return body;
}

function buildPush(input, path, routeId, variantId) {
  validateInput(input);
  return {
    routeId,
    variantId,
    request: { method: 'POST', path, body: pushBody(input) },
    expected: { ...expected(input, 0), fetchUrl: input.release.downloadPath },
  };
}

export function buildNativePushTrigger(input) {
  return buildPush(input, '/api/release/push', 'R18', 'accepted');
}

export function buildCompatiblePushTrigger(input) {
  return buildPush(input, '/api/v3/release/push', 'R19', 'response contract');
}

export function buildAllAutomaticSearchTrigger(input) {
  validateInput(input);
  return {
    routeId: 'R08',
    variantId: 'single league',
    label: 'all monitored automatic search with one seeded league',
    request: { method: 'POST', path: '/api/automatic-search/all', body: {} },
    expected: expected(input, { minimum: 1 }),
  };
}

export function classifyPushResponse({ status, body }) {
  if (status !== 200) throw new Error(`push response must have status 200, received ${status}`);
  if (!Array.isArray(body) || body.length !== 1 || !body[0] || typeof body[0] !== 'object') {
    throw new Error('push response must contain exactly one release');
  }
  const result = body[0];
  if (![result.approved, result.temporarilyRejected, result.rejected].every((value) => typeof value === 'boolean')) {
    throw new Error('push response outcome flags must be boolean');
  }
  const states = [result.approved === true, result.temporarilyRejected === true, result.rejected === true];
  if (states.filter(Boolean).length !== 1) throw new Error('push response must set exactly one outcome flag');
  const rejections = Array.isArray(result.rejections) ? result.rejections : [];
  if (result.approved === true) {
    if (rejections.length !== 0) throw new Error('accepted push response must have no rejections');
    return 'accepted';
  }
  if (rejections.length === 0 || rejections.some((reason) => typeof reason !== 'string' || reason.length === 0)) {
    throw new Error('non-accepted push response must contain a rejection reason');
  }
  if (result.temporarilyRejected === true) return 'delayed';
  return 'rejected';
}
