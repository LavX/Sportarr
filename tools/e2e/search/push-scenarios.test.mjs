import assert from 'node:assert/strict';
import { test } from 'node:test';

import {
  buildAllAutomaticSearchTrigger,
  buildCompatiblePushTrigger,
  buildNativePushTrigger,
  classifyPushResponse,
} from './push-scenarios.mjs';

const release = Object.freeze({
  title: 'Formula1 2026 Belgian Grand Prix Race 1080p WEB-DL AAC2.0 H.264-FIXTURE',
  guid: 'fixture-release-belgian-race',
  size: 1048576,
  publishDate: '2026-07-26T16:00:00.000Z',
  downloadPath: 'http://sv-b0-fixture:9080/payload/fixture.torrent',
  infoHash: '0123456789abcdef0123456789abcdef01234567',
});

test('native torrent push uses the accepted endpoint fields and direct fixture payload URL', () => {
  const trigger = buildNativePushTrigger({ eventId: 42, leagueId: 7, release, clientType: 'qbittorrent' });
  assert.equal(trigger.routeId, 'R18');
  assert.equal(trigger.variantId, 'accepted');
  assert.deepEqual(trigger.request, {
    method: 'POST',
    path: '/api/release/push',
    body: {
      title: release.title,
      downloadUrl: release.downloadPath,
      magnetUrl: `magnet:?xt=urn:btih:${release.infoHash}`,
      indexer: 'search-validation-fixture',
      downloadProtocol: 'torrent',
      size: release.size,
      publishDate: release.publishDate,
    },
  });
  assert.deepEqual(trigger.expected, {
    eventId: 42,
    leagueId: 7,
    fetchUrl: release.downloadPath,
    fixtureReleaseGuid: release.guid,
    sourceSearchRequests: 0,
    payloadDownloadRequired: true,
    allowedOutcomes: ['accepted', 'delayed', 'rejected'],
  });
});

test('compatible Usenet push sends the NZB URL and protocol accepted by the shared handler', () => {
  const usenet = { ...release, downloadPath: 'http://sv-b0-fixture:9080/payload/fixture.nzb' };
  const trigger = buildCompatiblePushTrigger({ eventId: 42, leagueId: 7, release: usenet, clientType: 'sabnzbd' });
  assert.equal(trigger.routeId, 'R19');
  assert.equal(trigger.variantId, 'response contract');
  assert.equal(trigger.request.path, '/api/v3/release/push');
  assert.equal(trigger.request.body.downloadProtocol, 'usenet');
  assert.equal(trigger.request.body.downloadUrl, usenet.downloadPath);
  assert.equal(Object.hasOwn(trigger.request.body, 'magnetUrl'), false);
  assert.equal(trigger.expected.sourceSearchRequests, 0);
  assert.equal(trigger.expected.payloadDownloadRequired, true);
});

test('all automatic search labels its one-league fixture coverage honestly', () => {
  const trigger = buildAllAutomaticSearchTrigger({ eventId: 42, leagueId: 7, release, clientType: 'qbittorrent' });
  assert.deepEqual(trigger, {
    routeId: 'R08',
    variantId: 'single league',
    label: 'all monitored automatic search with one seeded league',
    request: { method: 'POST', path: '/api/automatic-search/all', body: {} },
    expected: {
      eventId: 42,
      leagueId: 7,
      fixtureReleaseGuid: release.guid,
      sourceSearchRequests: { minimum: 1 },
      payloadDownloadRequired: true,
      allowedOutcomes: ['accepted', 'rejected'],
    },
  });
});

test('push response classification distinguishes accepted, delayed, and rejected outcomes', () => {
  assert.equal(classifyPushResponse({ status: 200, body: [{ approved: true, rejected: false, temporarilyRejected: false, rejections: [] }] }), 'accepted');
  assert.equal(classifyPushResponse({ status: 200, body: [{ approved: false, rejected: false, temporarilyRejected: true, rejections: ['Held by delay profile until 17:00'] }] }), 'delayed');
  assert.equal(classifyPushResponse({ status: 200, body: [{ approved: false, rejected: true, temporarilyRejected: false, rejections: ['No monitored event matched this release title'] }] }), 'rejected');
});

test('push response classification rejects invalid status and contradictory flags', () => {
  assert.throws(() => classifyPushResponse({ status: 400, body: [] }), /status 200/);
  assert.throws(() => classifyPushResponse({ status: 200, body: [] }), /one release/);
  assert.throws(() => classifyPushResponse({ status: 200, body: [{ approved: true, rejected: true, temporarilyRejected: false }] }), /exactly one/);
  assert.throws(() => classifyPushResponse({ status: 200, body: [{ approved: false, rejected: false, temporarilyRejected: false }] }), /exactly one/);
  assert.throws(() => classifyPushResponse({ status: 200, body: [{ approved: true, rejected: false, temporarilyRejected: false, rejections: ['wrong'] }] }), /accepted.*rejections/);
  assert.throws(() => classifyPushResponse({ status: 200, body: [{ approved: false, rejected: true, temporarilyRejected: false, rejections: [] }] }), /rejection reason/);
});

test('builders reject incomplete IDs, unsafe fixture URLs, and invalid release fields', () => {
  assert.throws(() => buildNativePushTrigger({ eventId: 0, leagueId: 7, release, clientType: 'qbittorrent' }), /eventId/);
  assert.throws(() => buildNativePushTrigger({ eventId: 42, leagueId: 7, release, clientType: 'other' }), /clientType/);
  assert.throws(() => buildNativePushTrigger({ eventId: 42, leagueId: 7, release: { ...release, downloadPath: 'https://example.com/file' }, clientType: 'qbittorrent' }), /fixture/);
  assert.throws(() => buildNativePushTrigger({ eventId: 42, leagueId: 7, release: { ...release, infoHash: 'bad' }, clientType: 'qbittorrent' }), /infoHash/);
  assert.throws(() => buildCompatiblePushTrigger({ eventId: 42, leagueId: 7, release: { ...release, publishDate: 'bad' }, clientType: 'sabnzbd' }), /publishDate/);
});
