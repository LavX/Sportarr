#!/usr/bin/env node
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { createFixtureServer, createSearchBarrier } from './fixtures.mjs';
import { createClientProxy } from './client-proxy.mjs';
import { classifyPushResponse } from './push-scenarios.mjs';
import { getScenario } from './scenarios.mjs';
import { sabJobs, sabOrigin, sabKey, verifySabTransfer, isRetryableSabTransferError, readContainedFile } from './sab.mjs';
import { createUsenetFixture, createNntpServer } from './usenet.mjs';

export function validateSmokeTarget({ app, root }) {
  const url = new URL(app);
  assert.equal(url.origin, 'http://sv-b0-app:1867');
  assert.equal(url.pathname, '/');
  assert.equal(url.search, '');
  assert.equal(url.hash, '');
  assert.equal(url.username, '');
  assert.equal(url.password, '');
  assert.equal(root, '/data/e2e-lib');
}

export async function runSmoke({ app, root, output, scenario = 'manual', clientType = 'qbittorrent', apiKey }) {
  validateSmokeTarget({ app, root });
  const journey = getScenario(scenario);
  assert.ok(apiKey, 'isolated API key is required');
  await mkdir(output, { recursive: false });
  const transcript = [];
  const save = (name, value) => writeFile(resolve(output, `${name}.json`), JSON.stringify(value, null, 2).replaceAll(sabKey, '[isolated-sab-key]'));
  async function request(path, body, base = app, method) {
    const response = await fetch(new URL(path, base), {
      method: method ?? (body === undefined ? 'GET' : 'POST'),
      headers: { 'Content-Type': 'application/json', ...(base === app ? { 'X-Api-Key': apiKey } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(base === 'http://sv-browser:9082' ? 120000 : 30000),
    });
    const text = await response.text();
    let data;
    try { data = JSON.parse(text); } catch { data = text; }
    transcript.push({ path, base, body: base === 'http://sv-browser:9082' ? { ...body, apiKey: '[isolated-key]' } : body, status: response.status, data });
    await save('http', transcript);
    if (!response.ok) {
      const error = new Error(`${path} returned ${response.status}: ${text.slice(0, 500)}`);
      error.failureCategory = base === app && phase === 'acquisition' ? 'acquisition' : 'infrastructure';
      throw error;
    }
    return data;
  }
  assert.ok(['qbittorrent', 'sabnzbd'].includes(clientType));
  let server;
  const searchBarrier = journey.repeatTrigger ? createSearchBarrier({ timeoutMs: 10000 }) : null;
  let clientProxy;
  let nntp;
  let phase = 'setup';
  const nntpConnections = new Set();
  try {
    for (const path of ['/api/events', '/api/indexer', '/api/downloadclient', '/api/rootfolder']) {
      assert.equal((await request(path)).length, 0, `${path} must be empty for an independent smoke case`);
    }
    clientProxy = createClientProxy({ clientType });
    await new Promise((done, reject) => { clientProxy.once('error', reject); clientProxy.listen(9081, '0.0.0.0', done); });
    const qbit = 'http://sv-b0-qbit:8080';
    const getClientJobs = async () => {
      try { return clientType === 'sabnzbd' ? await sabJobs(request) : await request('/api/v2/torrents/info', undefined, qbit); }
      catch (error) { error.failureCategory = 'infrastructure'; throw error; }
    };
    assert.equal((await getClientJobs()).length, 0);
    if (clientType === 'sabnzbd') {
      const queue = await request(`/api?mode=queue&output=json&apikey=${sabKey}`, undefined, sabOrigin);
      assert.equal(queue.queue.slots.length, 0);
    }
    const { createTorrent } = await import('./torrent.mjs');
    const name = 'UFC.9999.2026.09.01.Main.Card.720p.WEB-DL.H264-SEARCHFIXTURE.mkv';
    const data = await readFile(`${root}/payload/fixture.mkv`);
    await copyFile(`${root}/payload/fixture.mkv`, `${root}/payload/${name}`);
    const torrent = createTorrent({ data, name, webSeedUrl: `http://sv-b0-fixture:9080/payload/${name}` });
    await writeFile(`${root}/payload/fixture.torrent`, torrent.torrent);
    let usenet;
    if (clientType === 'sabnzbd') {
      usenet = createUsenetFixture({ data, name });
      await writeFile(`${root}/payload/fixture.nzb`, usenet.nzb);
      nntp = createNntpServer({ articles: usenet.articles });
      nntp.on('connection', socket => { nntpConnections.add(socket); socket.on('close', () => nntpConnections.delete(socket)); });
      await new Promise((done, reject) => { nntp.once('error', reject); nntp.listen(1190, '0.0.0.0', done); });
      await save('nntp-source', { articleIds: usenet.articleIds, crc32: usenet.crc32, size: usenet.size });
    }
    const release = { guid: 'synthetic-ufc-9999-main', title: name.slice(0, -4), size: data.length,
      publishDate: '2026-09-06T00:00:00Z', category: 5060, seeders: 10,
      ...(usenet ? {} : { infoHash: torrent.infoHash }), downloadPath: usenet ? 'fixture.nzb' : 'fixture.torrent' };
    const corpus = { synthetic: true, sourceSemantics: 'and', sourceQuota: 20, releases: [release], expectedSha256: createHash('sha256').update(data).digest('hex') };
    await save('corpus', corpus);
    server = createFixtureServer({ releases: [release], payloadRoot: `${root}/payload`, payloadFiles: [name], searchMode: 'and', quota: 20 }, { beforeSearchResponse: searchBarrier?.wait });
    await new Promise((done, reject) => { server.once('error', reject); server.listen(9080, '0.0.0.0', done); });
    const rootFolder = await request('/api/rootfolder', { path: `${root}/library` });
    const definitions = await request('/api/qualitydefinition');
    for (const definition of definitions) {
      await request(`/api/qualitydefinition/${definition.id}`, { ...definition, minSize: 0 }, app, 'PUT');
    }
    await save('fixture-settings', { purpose: 'Allow the three-second generated payload in positive acquisition cases',
      qualityDefinitions: await request('/api/qualitydefinition') });
    const profiles = await request('/api/qualityprofile');
    assert.ok(profiles.length > 0);
    const profile = profiles.find(item => item.name === 'Any') ?? profiles[0];
    const league = await request('/api/leagues', { name: 'UFC', sport: 'Fighting', rootFolderId: rootFolder.id,
      qualityProfileId: profile.id, monitored: true, enableDvr: false, monitorType: 'All',
      searchForMissingEvents: false, searchForCutoffUnmetEvents: false, monitoredParts: 'Main Card' });
    const leagueId = league.id ?? league.league?.id;
    assert.ok(leagueId, 'league response must identify the created league');
    if (clientType === 'sabnzbd') await request('/api/downloadclient', { name: 'Isolated SABnzbd', type: 5, host: 'sv-b0-fixture', port: 9081, apiKey: sabKey,
      category: 'sv-fixture', enabled: true, removeCompletedDownloads: false, removeFailedDownloads: false });
    else await request('/api/downloadclient', { name: 'Isolated qBittorrent', type: 0, host: 'sv-b0-fixture', port: 9081,
      category: 'sv-fixture', enabled: true, removeCompletedDownloads: false, removeFailedDownloads: false,
      directory: `${root}/downloads` });
    await request('/api/indexer', { name: usenet ? 'Isolated Newznab' : 'Isolated Torznab', implementation: usenet ? 'Newznab' : 'Torznab', enable: true,
      enableRss: journey.rss === true, enableAutomaticSearch: true, enableInteractiveSearch: true,
      fields: [{ name: 'baseUrl', value: 'http://sv-b0-fixture:9080' }, { name: 'apiPath', value: '/api' },
        { name: 'apiKey', value: 'fixture-key' }, { name: 'categories', value: [5060] }] });
    const started = performance.now();
    const event = await request('/api/events', { title: 'UFC 9999', sport: 'Fighting', leagueId,
      eventDate: '2026-09-01T18:00:00Z', season: '2026', monitored: true, status: 'Completed',
      qualityProfileId: profile.id, searchOnAdd: scenario === 'search-on-add' });
    const eventId = event.id ?? event.event?.id;
    assert.ok(eventId, 'event response must identify the created event');
    phase = 'acquisition';
    let trigger;
    let previousRssTaskIds = [];
    if (journey.browser) {
      trigger = await request('/journey', { eventId, leagueId, title: release.title, scenario, apiKey }, 'http://sv-browser:9082');
    } else if (journey.interactive) {
      const searchRequest = journey.request({ eventId, leagueId, clientType, release: { ...release, downloadPath: `http://sv-b0-fixture:9080/payload/${release.downloadPath}` } });
      const search = await request(searchRequest.path, searchRequest.body);
      await save('search', search);
      const rows = Array.isArray(search) ? search : search.releases ?? search.results;
      assert.ok(Array.isArray(rows), 'search must return a release list');
      const selected = rows.find(row => row.title === release.title);
      assert.ok(selected, 'query must retrieve the expected release from the catalogue');
      if (scenario === 'manual-warm') {
        const before = await request('/_fixture/requests', undefined, 'http://sv-b0-fixture:9080');
        const warm = await request(`/api/event/${eventId}/search`, {});
        const after = await request('/_fixture/requests', undefined, 'http://sv-b0-fixture:9080');
        const warmRows = Array.isArray(warm) ? warm : warm.releases ?? warm.results;
        assert.ok(warmRows.some(row => row.title === release.title));
        await save('warm-cache', { coldRequests: before.length, warmAdditionalRequests: after.length - before.length,
          cold: rows, warm: warmRows });
      }
      trigger = await request('/api/release/grab', { ...selected, eventId });
    } else if (journey.request) {
      if (journey.task === 'RssSync') previousRssTaskIds = (await request('/api/task')).map(task => task.id);
      const triggerRequest = journey.request({ eventId, leagueId, clientType, release: { ...release, downloadPath: `http://sv-b0-fixture:9080/payload/${release.downloadPath}` } });
      trigger = await request(triggerRequest.path, triggerRequest.body);
      if (journey.push) {
        await save('push-outcome', { outcome: classifyPushResponse({ status: 200, body: trigger }), response: trigger });
        assert.equal(classifyPushResponse({ status: 200, body: trigger }), 'accepted', 'the valid fixture push must be accepted');
      }
      if (journey.repeatTrigger) {
        assert.equal(trigger.queued, 1, 'the initial Wanted action must queue the one missing event');
        let entryTimer;
        try {
          await Promise.race([searchBarrier.entered, new Promise((_, reject) => {
            entryTimer = setTimeout(() => reject(new Error('the source did not enter the controlled repeat-click window')), 10000);
          })]);
          if (!searchBarrier.pending) throw new Error('the controlled source window expired before repeat click');
          const repeated = await request(triggerRequest.path, triggerRequest.body);
          await save('repeat-trigger', repeated);
          if (!searchBarrier.pending) throw new Error('the controlled source window expired during repeat click');
          await save('scheduling-control', { firstSearchResponseHeld: searchBarrier.pending, maxHoldMs: 10000, repeatClickCompletedWhileHeld: searchBarrier.pending });
          assert.equal(repeated.queued, 0, 'repeat click must not queue another search while the first search is held');
          assert.equal(repeated.skippedAlreadyQueued, 1, 'repeat click must find the held active search');
        } finally { clearTimeout(entryTimer); searchBarrier.release(); }
      }
    }
    await save('trigger', { scenario, eventId, leagueId, trigger });
    if (journey.task === 'RssSync') {
      let completed = false;
      for (let attempt = 0; attempt < 15; attempt++) {
        const tasks = await request('/api/task');
        await save('tasks', tasks);
        const created = tasks.filter(task => task.commandName === 'RssSync' && !previousRssTaskIds.includes(task.id));
        assert.ok(created.length <= 1, 'RSS task identity must be unambiguous');
        completed = created.length === 1 && created[0].status === 'Completed';
        if (completed) break;
        await delay(1000);
      }
      assert.ok(completed, 'the requested RSS task must reach completion');
      const requests = await request('/_fixture/requests', undefined, 'http://sv-b0-fixture:9080');
      await save('source-requests', requests);
      await save('client', await getClientJobs());
      await save('files', await request(`/api/events/${eventId}/files`));
      assert.ok(requests.some(row => row.query?.t === 'search'),
        'the completed RSS task must issue an actual feed request');
    }
    let files = [];
    let completedQueue = [];
    let sabTransfer;
    const transferBudgetMs = 90000;
    const backgroundInitialDelayMs = 120000;
    const deadline = performance.now() + transferBudgetMs + (scenario === 'rss-background' ? backgroundInitialDelayMs : 0);
    await save('wait-budget', { transferBudgetMs, backgroundInitialDelayMs, startupPolling: false });
    while (performance.now() < deadline) {
      files = await request(`/api/events/${eventId}/files`);
      const currentJobs = await getClientJobs();
      await save('client', currentJobs);
      if (usenet && !sabTransfer && currentJobs.length === 1 && currentJobs[0].status === 'Completed') {
        const queue = await request('/api/queue');
        if (queue.length === 1) {
          try { sabTransfer = await verifySabTransfer(currentJobs, { root, name, data, queue: queue[0], importedFile: files[0] }); }
          catch (error) { if (!isRetryableSabTransferError(error)) throw error; }
          if (sabTransfer) await save('client-transfer', sabTransfer);
        }
      }
      completedQueue = await request('/api/queue');
      await save('queue', completedQueue);
      if ((!usenet || sabTransfer) && files.length > 0 && completedQueue.some(row => row.eventId === eventId && row.status === 7)) break;
      await delay(2000);
    }
    await save('tasks', await request('/api/task'));
    const sourceRequests = await request('/_fixture/requests', undefined, 'http://sv-b0-fixture:9080');
    await save('source-requests', sourceRequests);
    await save('files', files);
    await save('timing', { elapsedMs: performance.now() - started });
    assert.equal(files.length, 1, 'one real imported event file is required');
    if (journey.push) assert.equal(sourceRequests.length, 0, 'this first-file push must not perform an indexer search');
    else assert.ok(sourceRequests.some(row => row.status === 200 && row.releaseGuids.includes(release.guid)),
      'the source must actually return the selected release');
    const payloadRequests = await request('/_fixture/payload-requests', undefined, 'http://sv-b0-fixture:9080');
    await save('payload-requests', payloadRequests);
    assert.ok(payloadRequests.some(row => row.path === `/payload/${release.downloadPath}` && row.method === 'GET' && row.status === 200 && row.finished), 'the source must serve the actual download descriptor');
    assert.ok(sourceRequests.every(row => row.status !== 429), 'the source must not report throttling in this smoke case');
    assert.equal(files[0].eventId, eventId);
    assert.ok(files[0].filePath.startsWith(`${root}/library/`));
    const imported = await readContainedFile(files[0].filePath, `${root}/library`);
    const actualHash = createHash('sha256').update(imported).digest('hex');
    assert.equal(actualHash, corpus.expectedSha256, 'imported bytes must match the generated payload');
    const clientJobs = await getClientJobs();
    await save('client', clientJobs);
    assert.equal(clientJobs.length, 1, 'the isolated client must contain exactly one job');
    if (usenet) {
      assert.ok(sabTransfer, 'SAB output bytes must be verified in the client output or imported library');
      assert.equal(clientJobs[0].nzo_id, sabTransfer.jobId);
      assert.equal(completedQueue[0].downloadId, sabTransfer.jobId);
      assert.equal(clientJobs[0].status, 'Completed');
    } else {
    assert.equal(clientJobs[0].hash, torrent.infoHash);
    assert.ok(clientJobs[0].downloaded >= data.length, 'the client must transfer the actual bytes');
    assert.equal(clientJobs[0].completed, data.length);
    assert.equal(clientJobs[0].progress, 1);
    }
    assert.equal(completedQueue.length, 1, 'the app must track exactly one download');
    assert.equal(completedQueue[0].eventId, eventId);
    assert.equal(completedQueue[0].status, 7, 'the app must report Imported');
    await save('transfer', { scenario, eventId, filePath: files[0].filePath, ...(usenet ? {} : { infoHash: torrent.infoHash }),
      expectedHash: corpus.expectedSha256, actualHash, bytes: imported.length,
      expectedPart: journey.expectedPart, expectedPartBasis: 'Known synthetic payload content', actualPart: files[0].partName, clientType,
      note: 'Transfer and file identity checked. Media probe, UI, and full route coverage are separate gates.' });
    assert.equal(clientProxy.requests.filter(item => item.kind === 'add' && item.accepted).length, 1, 'the app must issue exactly one accepted client add');
    if (Object.hasOwn(journey, 'expectedPart')) assert.equal(files[0].partName, journey.expectedPart, 'the imported part must match the known fixture content');
    return 0;
  } catch (error) {
    await save('failure', { message: error.message, stack: error.stack, code: error.code, phase,
      failureCategory: error.failureCategory ?? (phase === 'acquisition' && error.code === 'ERR_ASSERTION' ? 'acquisition' : 'infrastructure') });
    throw error;
  } finally {
    searchBarrier?.release();
    if (clientProxy) { clientProxy.closeAllConnections(); await new Promise(done => clientProxy.close(done)); await save('client-requests', clientProxy.requests); }
    if (nntp) { for (const socket of nntpConnections) socket.destroy(); await new Promise(done => nntp.close(done)); await save('nntp-requests', nntp.requests); await save('nntp-connection-errors', nntp.connectionErrors); }
    if (server) { server.closeAllConnections(); await new Promise(done => server.close(done)); }
  }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const args = process.argv.slice(2);
  const value = (key, fallback) => args.includes(key) ? args[args.indexOf(key) + 1] : fallback;
  runSmoke({ app: value('--app', 'http://sv-b0-app:1867'), root: '/data/e2e-lib', output: value('--output'),
    scenario: value('--scenario', 'manual'), clientType: value('--client', 'qbittorrent'), apiKey: process.env.SEARCH_FIXTURE_API_KEY })
    .then(code => { process.exitCode = code; }).catch(error => { console.error(error.message); process.exitCode = 1; });
}
