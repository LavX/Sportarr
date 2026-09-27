import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';

import { collectRun, collectRuns } from './collect.mjs';

async function fixture(scenario = 'manual', options = {}) {
  const dir = await mkdtemp(join(tmpdir(), 'search-smoke-'));
  await mkdir(join(dir, 'case'));
  const eventId = 7;
  const hash = 'a'.repeat(64);
  const files = options.files ?? [{ eventId, filePath: '/data/e2e-lib/library/event.mkv', size: 123 }];
  const requests = options.requests ?? [{ attempt: 1, releaseGuids: ['release-1'] }];
  const values = {
    'inputs.json': { runId: 'run-1', scenario, provider: 'sqlite', config: { buildSha: '029eb3e4ebb93acfd10c35784247b32f49b59c84', images: { app: `sha256:${'b'.repeat(64)}` }, archiveHashes: { payloadFile: hash } } },
    'case/corpus.json': { releases: [{ guid: 'release-1', size: 123 }], expectedSha256: hash },
    'case/http.json': [{ path: `/api/event/${eventId}/search`, data: { results: [{ rejections: options.sizeRejected ? ['Size is below minimum profile'] : [] }] } }, { path: '/api/release/grab', body: { guid: 'release-1' } }],
    'case/files.json': files,
    'case/source-requests.json': requests,
    'case/trigger.json': { scenario, eventId, trigger: { success: true } },
    'case/client.json': files.length ? [{ completed: 123, downloaded: 123 }] : [],
    'case/queue.json': [],
    'case/timing.json': { elapsedMs: 42 },
    'case/transfer.json': { eventId, filePath: '/data/e2e-lib/library/event.mkv', expectedHash: hash, actualHash: hash, bytes: 123 },
    'probe.json': { streams: [{ codec_type: 'video' }] },
    'decode.json': { status: 0 },
    'database-files.json': files.map(file => ({ EventId: file.eventId, FilePath: file.filePath, Size: file.size, Exists: 1 })),
    'smoke-outcome.json': options.outcome ?? { status: 'PASS', scenario, fileHash: hash, mediaProbe: true, mediaDecode: true, fullMatrix: 'INCOMPLETE' },
    'cleanup.json': [],
    'build-evidence.json': { sourceSha: '029eb3e4ebb93acfd10c35784247b32f49b59c84', hashes: { 'publish.tar.gz': 'c'.repeat(64) }, provenance: 'test build-session provenance' },
  };
  await Promise.all(Object.entries(values).filter(([path]) => !(options.omit ?? []).includes(path)).map(async ([path, value]) => writeFile(join(dir, path), `${JSON.stringify(value)}\n`)));
  await writeFile(join(dir, 'app.log'), options.appLog ?? '');
  return dir;
}

test('collects verified import evidence while leaving unknown counters null', async () => {
  const run = await collectRun(await fixture());
  assert.equal(run.result.status, 'PASS');
  assert.equal(run.result.duplicateAdds, null);
  assert.equal(run.result.quotaViolations, null);
  assert.equal(run.result.actualClientBytes, 123);
  assert.equal(run.result.fileChecks[0].databaseEventId, 7);
  assert.equal(run.result.fileChecks[0].probeVerified, true);
  assert.equal(run.result.fileChecks[0].decodeVerified, true);
});

test('keeps a size-rejected automatic failure as a defect', async () => {
  const dir = await fixture('automatic', { files: [], sizeRejected: true, outcome: { status: 'FAIL', scenario: 'automatic', message: 'one real imported event file is required', fullMatrix: 'INCOMPLETE' } });
  const run = await collectRun(dir);
  assert.equal(run.result.status, 'FAIL');
  assert.equal(run.result.limitation, null);
  assert.equal(run.result.defect, true);
});

test('keeps an app-log-only size rejection as a failure', async () => {
  const dir = await fixture('automatic', { files: [], appLog: '[Size Validation] REJECTED: fixture - Size 0.00GB below minimum 1.76GB\n', outcome: { status: 'FAIL', scenario: 'automatic', message: 'one real imported event file is required', fullMatrix: 'INCOMPLETE' } });
  const run = await collectRun(dir);
  assert.equal(run.result.status, 'FAIL');
  assert.equal(run.result.limitation, null);
});

test('records an RSS task with no source requests as a failure', async () => {
  const dir = await fixture('rss-task', { files: [], requests: [], outcome: { status: 'FAIL', scenario: 'rss-task', message: 'one real imported event file is required', fullMatrix: 'INCOMPLETE' } });
  const run = await collectRun(dir);
  assert.equal(run.result.status, 'FAIL');
  assert.equal(run.result.failure, 'RSS_TASK_NO_SOURCE_REQUESTS');
});

test('overrides a contradictory RSS pass when no source request exists', async () => {
  const dir = await fixture('rss-task', { files: [], requests: [], outcome: { status: 'PASS', scenario: 'rss-task', mediaProbe: true, mediaDecode: true, fullMatrix: 'INCOMPLETE' } });
  const run = await collectRun(dir);
  assert.equal(run.result.status, 'FAIL');
  assert.equal(run.result.defect, true);
});

test('does not rewrite an unrelated automatic failure as a size limitation', async () => {
  const dir = await fixture('automatic', { files: [], outcome: { status: 'FAIL', scenario: 'automatic', message: 'worker crashed', fullMatrix: 'INCOMPLETE' } });
  const run = await collectRun(dir);
  assert.equal(run.result.status, 'FAIL');
  assert.equal(run.result.limitation, null);
});

test('blocks a run when required import evidence is missing', async () => {
  const dir = await fixture();
  await writeFile(join(dir, 'probe.json'), 'null\n');
  const run = await collectRun(dir);
  assert.equal(run.result.status, 'BLOCKED');
  assert.match(run.result.blockedReason, /probe/);
});

test('writes a baseline ledger and snapshots explicitly passed runs', async () => {
  const output = await mkdtemp(join(tmpdir(), 'search-ledger-parent-'));
  const target = join(output, 'ledger');
  const collected = await collectRuns([await fixture()], target);
  assert.equal(collected.results.length, 1);
  assert.equal(JSON.parse(await readFile(join(target, 'results.json'), 'utf8')).length, 1);
  assert.equal(JSON.parse(await readFile(join(target, 'manifest.json'), 'utf8')).mode, 'baseline');
  assert.ok(JSON.parse(await readFile(join(target, 'evidence-ledger.json'), 'utf8')).files.length > 0);
  assert.equal(JSON.parse(await readFile(join(target, 'summary.json'), 'utf8')).fullBenchmark, 'INCOMPLETE');
});

test('records only the scheduled RSS trigger action', async () => {
  const run = await collectRun(await fixture('rss-task', { files: [], requests: [], outcome: { status: 'FAIL', scenario: 'rss-task' } }));
  assert.deepEqual(run.result.actions, ['POST /api/task/scheduled/rss-sync/trigger']);
});

test('blocks unknown scenarios without assigning an acquisition route', async () => {
  const run = await collectRun(await fixture('unexpected-scenario'));
  assert.equal(run.result.status, 'BLOCKED');
  assert.equal(run.result.routeId, 'UNKNOWN');
});

test('preserves genuine failure when success-only artifacts are absent', async () => {
  const dir = await fixture('automatic', { files: [], omit: ['case/transfer.json', 'probe.json', 'decode.json'], outcome: { status: 'FAIL', scenario: 'automatic', message: 'worker failed' } });
  const run = await collectRun(dir);
  assert.equal(run.result.status, 'FAIL');
  assert.equal(run.result.defect, true);
  assert.match(run.result.evidenceContext, /missing case\/transfer.json/);
});

test('maps queue and search-on-add to their own routes', async () => {
  assert.equal((await collectRun(await fixture('queue'))).result.routeId, 'R09');
  assert.equal((await collectRun(await fixture('search-on-add'))).result.routeId, 'R13');
});

test('derives selected payload identity for successful automatic acquisition', async () => {
  const run = await collectRun(await fixture('automatic'));
  assert.deepEqual(run.result.selectedReleaseIds, ['release-1']);
});

test('keeps a failed run with no corpus in a multi-run bundle', async () => {
  const failed = await fixture('automatic', { files: [], omit: ['case/corpus.json', 'case/transfer.json', 'probe.json', 'decode.json'], outcome: { status: 'FAIL', scenario: 'automatic', message: 'startup failed' } });
  const output = join(await mkdtemp(join(tmpdir(), 'search-mixed-')), 'ledger');
  const result = await collectRuns([failed, await fixture()], output);
  assert.equal(result.results.length, 2);
  assert.equal(result.results[0].status, 'FAIL');
});

test('writes candidate mode explicitly', async () => {
  const output = join(await mkdtemp(join(tmpdir(), 'search-candidate-')), 'ledger');
  const result = await collectRuns([await fixture()], output, { mode: 'candidate' });
  assert.equal(result.manifest.mode, 'candidate');
});

test('accepts redundant downloaded bytes when completed bytes are exact', async () => {
  const dir = await fixture();
  await writeFile(join(dir, 'case/client.json'), '[{"completed":123,"downloaded":130}]\n');
  assert.equal((await collectRun(dir)).result.status, 'PASS');
});

test('SAB completion timestamp is not treated as a byte count', async () => {
  const dir = await fixture('automatic');
  const inputs = JSON.parse(await readFile(join(dir, 'inputs.json')));
  inputs.config.clientType = 'sabnzbd';
  await writeFile(join(dir, 'inputs.json'), JSON.stringify(inputs));
  await writeFile(join(dir, 'case/client.json'), JSON.stringify([{ nzo_id: 'job-one', status: 'Completed', completed: 1788741665, bytes: 150 }]));
  assert.equal((await collectRun(dir)).result.status, 'BLOCKED');
  await writeFile(join(dir, 'case/client-transfer.json'), JSON.stringify({ jobId: 'job-one', completed: 123, downloaded: 150, fileHash: 'a'.repeat(64) }));
  const run = await collectRun(dir);
  assert.equal(run.result.status, 'PASS');
  assert.equal(run.result.actualClientBytes, 123);
  assert.equal(run.result.clientType, 'sabnzbd');
  assert.equal(run.result.sourceType, 'newznab');
});

test('interrupted and cleanup runs are blocked instead of product defects', async () => {
  for (const failureCategory of ['interrupted', 'cleanup']) {
    const dir = await fixture('automatic', { outcome: { status: 'FAIL', failureCategory } });
    const run = await collectRun(dir);
    assert.equal(run.result.status, 'BLOCKED');
    assert.equal(run.result.defect, false);
  }
});

test('client add and source quota counters come from observed requests', async () => {
  const dir = await fixture();
  await writeFile(join(dir, 'case/client-requests.json'), JSON.stringify([{ kind: 'add', accepted: true }, { kind: 'add', accepted: false }, { kind: 'other', accepted: false }, { kind: 'add', accepted: true }]));
  const corpus = JSON.parse(await readFile(join(dir, 'case/corpus.json')));
  corpus.sourceQuota = 0;
  await writeFile(join(dir, 'case/corpus.json'), JSON.stringify(corpus));
  const result = (await collectRun(dir)).result;
  assert.equal(result.clientAdds, 2);
  assert.equal(result.duplicateAdds, 1);
  assert.equal(result.quotaViolations, 1);
});

test('candidate binary fingerprints are separate from unchanged benchmark settings', async () => {
  const parent = await mkdtemp(join(tmpdir(), 'search-build-comparison-'));
  const runs = [];
  for (const marker of ['b', 'c']) {
    const dir = await fixture();
    const inputs = JSON.parse(await readFile(join(dir, 'inputs.json')));
    inputs.archiveHashes = { payloadFile: 'a'.repeat(64), configXml: 'd'.repeat(64), publishArchive: marker.repeat(64), buildEvidenceFile: marker.repeat(64) };
    await writeFile(join(dir, 'inputs.json'), JSON.stringify(inputs));
    await collectRuns([dir], join(parent, marker), { mode: marker === 'b' ? 'baseline' : 'candidate' });
    runs.push(JSON.parse(await readFile(join(parent, marker, 'manifest.json'))));
  }
  assert.equal(runs[0].sharedSettings.hash, runs[1].sharedSettings.hash);
  assert.notEqual(runs[0].build.publishArchiveHash, runs[1].build.publishArchiveHash);
});

test('rejects mixed binaries inside one bundle even when the base commit matches', async () => {
  const parent = await mkdtemp(join(tmpdir(), 'search-mixed-binaries-'));
  const dirs = [];
  for (const marker of ['b', 'c']) {
    const dir = await fixture();
    const inputs = JSON.parse(await readFile(join(dir, 'inputs.json')));
    inputs.archiveHashes = { publishArchive: marker.repeat(64) };
    await writeFile(join(dir, 'inputs.json'), JSON.stringify(inputs));
    dirs.push(dir);
  }
  await assert.rejects(collectRuns(dirs, join(parent, 'ledger')), /build archives/);
});

test('does not infer a committed source state from missing provenance', async () => {
  const parent = await mkdtemp(join(tmpdir(), 'search-source-state-'));
  const result = await collectRuns([await fixture()], join(parent, 'ledger'));
  assert.equal(result.manifest.build.sourceState, null);
});

test('records wrong part identity and prevents a contradictory pass', async () => {
  const dir = await fixture('automatic');
  const transfer = JSON.parse(await readFile(join(dir, 'case/transfer.json')));
  Object.assign(transfer, { expectedPart: 'Main Card', actualPart: null });
  await writeFile(join(dir, 'case/transfer.json'), JSON.stringify(transfer));
  const result = (await collectRun(dir)).result;
  assert.equal(result.status, 'FAIL');
  assert.equal(result.failure, 'IMPORTED_PART_MISMATCH');
  assert.equal(result.expectedPart, 'Main Card');
  assert.equal(result.actualPart, null);
  assert.equal(result.unexpectedAssignments.length, 1);
});

test('carries the SAB file verification location into the ledger', async () => {
  const dir = await fixture();
  await writeFile(join(dir, 'case/client-transfer.json'), JSON.stringify({ verificationLocation: 'imported library after source move', originalStorage: '/data/e2e-lib/downloads/a' }));
  assert.equal((await collectRun(dir)).result.clientByteEvidence, 'imported library after source move');
});

test('blocks infrastructure failures and retains a separate cleanup problem', async () => {
  const dir = await fixture('rss-task', { requests: [], outcome: { status: 'FAIL', failureCategory: 'infrastructure', message: 'fetch failed', cleanupFailed: true } });
  const result = (await collectRun(dir)).result;
  assert.equal(result.status, 'BLOCKED');
  assert.equal(result.defect, false);
  assert.equal(result.cleanupFailed, true);
});

test('a product defect remains a failure when cleanup also fails', async () => {
  const dir = await fixture('rss-task', { requests: [], outcome: { status: 'FAIL', failureCategory: 'acquisition', cleanupFailed: true } });
  const result = (await collectRun(dir)).result;
  assert.equal(result.status, 'FAIL');
  assert.equal(result.failure, 'RSS_TASK_NO_SOURCE_REQUESTS');
  assert.equal(result.cleanupFailed, true);
});
