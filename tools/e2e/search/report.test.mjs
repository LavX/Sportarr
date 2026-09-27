import test from 'node:test';
import assert from 'node:assert/strict';

import { mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { compareRuns, ratio, determineExitCode, gatesEnabled, loadRun } from './report.mjs';
import routes from './routes.json' with { type: 'json' };

const baseManifest = {
  schemaVersion: 1, runId: 'run-1', mode: 'baseline',
  build: { sha: '029eb3e4ebb93acfd10c35784247b32f49b59c84', imageDigest: `sha256:${'a'.repeat(64)}` },
  corpus: { hash: 'corpus-1', path: 'corpus.json' },
  sharedSettings: { hash: 'settings-1', path: 'settings.json' },
  runner: { version: 'runner-1', sha: 'runner-sha' }, provenanceStatus: 'COMPLETE', providers: ['sqlite', 'postgres'],
  expectedCases: [], treatment: { featureFlags: [] },
};
const candidateManifest = { ...baseManifest, runId: 'run-2', mode: 'candidate', build: { sha: '1'.repeat(40), imageDigest: `sha256:${'b'.repeat(64)}` }, treatment: { featureFlags: ['catalogue'] } };

function row(status = 'PASS', overrides = {}) {
  return {
    routeId: 'R01', variantId: 'page and modal entry points', actions: ['POST /api/event/{id}/search', 'POST /api/release/grab'],
    expectation: 'IMPORT', caseId: 'case-1', provider: 'sqlite', sourceType: 'torznab',
    clientType: 'qbittorrent', cacheState: 'cold', buildSha: 'abc', status,
    expectedNeeds: ['need-1'], expectedReleaseIds: ['release-1'], returnedReleaseIds: ['release-1'], selectedReleaseIds: ['release-1'],
    importedNeeds: status === 'PASS' ? ['need-1'] : [], unexpectedAssignments: [], clientAdds: status === 'PASS' ? 1 : 0,
    duplicateAdds: 0, outboundAttempts: 2, quotaViolations: 0,
    fileChecks: status === 'PASS' ? [{ path: '/test/file', payloadId: 'payload', expectedHash: 'hash', actualHash: 'hash', contentVerified: true, insideTestRoot: true }] : [],
    elapsedMs: 10, releaseMetrics: { recall: 1, precision: 1 }, evidencePaths: [{ path: 'evidence.json', kind: 'case', exists: true }], ...overrides,
  };
}

function runs(baselineResults, candidateResults, candidateOverrides = {}) {
  return [
    { manifest: baseManifest, results: baselineResults.map(item => ({ ...item, buildSha: baseManifest.build.sha })) },
    { manifest: { ...candidateManifest, ...candidateOverrides }, results: candidateResults.map(item => ({ ...item, buildSha: candidateManifest.build.sha })) },
  ];
}

test('reports per-route and per-case deltas without requiring a passing baseline', () => {
  const comparison = compareRuns(...runs([row('FAIL')], [row()], { treatment: { featureFlags: ['catalogue', 'planner'] } }), { enforceGates: false });
  assert.equal(comparison.cases[0].change, 'IMPROVED');
  assert.equal(comparison.routes[0].passedDelta, 1);
  assert.equal(comparison.exitCode, 0);
});

for (const field of ['corpus', 'sharedSettings', 'runner']) {
  test(`refuses mismatched ${field}`, () => {
    assert.throws(() => compareRuns(
      ...runs([row()], [row()], { [field]: { ...candidateManifest[field], hash: 'different', version: 'different' } }),
    ), new RegExp(field));
  });
}

test('refuses undeclared shared setting differences even with matching hash', () => {
  assert.throws(() => compareRuns(
    ...runs([row()], [row()], { sharedSettings: { hash: 'settings-1', path: 'different-settings.json' } }),
  ), /sharedSettings/);
});

test('reports zero denominators as null', () => {
  assert.equal(ratio(0, 0), null);
  assert.equal(ratio(1, 2), 0.5);
});

test('returns incomplete before failed when required coverage is blocked or absent', () => {
  assert.equal(determineExitCode([{ required: true, outcome: 'INCOMPLETE' }, { required: true, outcome: 'FAIL' }]), 2);
  assert.equal(determineExitCode([{ required: true, outcome: 'FAIL' }]), 1);
  assert.equal(determineExitCode([{ required: true, outcome: 'PASS' }]), 0);
});

test('marks a missing candidate case incomplete', () => {
  const comparison = compareRuns(
    ...runs([row()], []),
  );
  assert.equal(comparison.cases[0].candidateStatus, 'MISSING');
  assert.equal(comparison.exitCode, 2);
});

test('does not let one passing variant cover its route siblings', () => {
  const comparison = compareRuns(
    ...runs([row()], [row()]),
  );
  assert.ok(comparison.routes.find(route => route.routeId === 'R01').missingVariants.length > 0);
  assert.equal(comparison.exitCode, 2);
});

test('inventory contains all 32 required route families with explicit actions and variants', () => {
  assert.deepEqual(routes.map(route => route.id), Array.from({ length: 32 }, (_, index) => `R${String(index + 1).padStart(2, '0')}`));
  assert.ok(routes.every(route => route.required === true));
  assert.ok(routes.every(route => route.actions.length > 0 && route.variants.length > 0));
});

test('emits measured case and route deltas', () => {
  const comparison = compareRuns(
    ...runs([row('PASS', { outboundAttempts: 4, elapsedMs: 30 })], [row('PASS', { outboundAttempts: 2, elapsedMs: 10 })]),
    { enforceGates: false },
  );
  assert.equal(comparison.cases[0].outboundAttemptsDelta, -2);
  assert.equal(comparison.cases[0].correctImportedNeedsDelta, 0);
  assert.equal(comparison.routes[0].outboundAttemptsDelta, -2);
  assert.equal(comparison.routes[0].elapsedMsDelta, -20);
});

test('rejects duplicate ledger keys', () => {
  assert.throws(() => compareRuns(...runs([row(), row()], [row()])), /duplicate/);
});

test('marks a frozen expected case missing from candidate results incomplete', () => {
  const expected = [{ routeId: 'R01', variantId: 'page and modal entry points', caseId: 'case-1', provider: 'sqlite', sourceType: 'torznab', clientType: 'qbittorrent', cacheState: 'cold' }];
  const comparison = compareRuns(
    { manifest: { ...baseManifest, expectedCases: expected }, results: [{ ...row(), buildSha: baseManifest.build.sha }] },
    { manifest: { ...candidateManifest, expectedCases: expected }, results: [] },
  );
  assert.equal(comparison.exitCode, 2);
});

test('loadRun verifies frozen input hashes from disk', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'search-report-'));
  const corpus = '{"cases":[]}\n';
  const settings = '{"language":"en"}\n';
  const hash = value => createHash('sha256').update(value).digest('hex');
  const diskManifest = { ...baseManifest, corpus: { hash: hash(corpus), path: 'corpus.json' }, sharedSettings: { hash: hash(settings), path: 'settings.json' } };
  await Promise.all([
    writeFile(join(directory, 'manifest.json'), JSON.stringify(diskManifest)),
    writeFile(join(directory, 'results.json'), '[]'),
    writeFile(join(directory, 'corpus.json'), corpus),
    writeFile(join(directory, 'settings.json'), settings),
    writeFile(join(directory, 'evidence-ledger.json'), '{"files":[],"fixedInputs":[]}\n'),
  ]);
  assert.equal((await loadRun(directory)).manifest.corpus.hash, hash(corpus));
  await writeFile(join(directory, 'corpus.json'), '{}');
  await assert.rejects(loadRun(directory), /corpus hash/);
});

test('loadRun verifies collected evidence ledger hashes', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'search-ledger-'));
  const corpus = '{}\n';
  const settings = '{}\n';
  const evidence = 'original\n';
  const hash = value => createHash('sha256').update(value).digest('hex');
  const diskManifest = { ...baseManifest, corpus: { hash: hash(corpus), path: 'corpus.json' }, sharedSettings: { hash: hash(settings), path: 'settings.json' } };
  await Promise.all([
    writeFile(join(directory, 'manifest.json'), JSON.stringify(diskManifest)), writeFile(join(directory, 'results.json'), '[]'),
    writeFile(join(directory, 'corpus.json'), corpus), writeFile(join(directory, 'settings.json'), settings),
    writeFile(join(directory, 'evidence.json'), evidence),
    writeFile(join(directory, 'evidence-ledger.json'), JSON.stringify({ files: [{ path: 'evidence.json', sha256: hash(evidence), bytes: Buffer.byteLength(evidence) }], fixedInputs: [] })),
  ]);
  await writeFile(join(directory, 'evidence.json'), 'changed\n');
  await assert.rejects(loadRun(directory), /evidence ledger hash/);
});

test('reports provider, source, client, and cache strata with meaningful percentages', () => {
  const comparison = compareRuns(...runs([row('FAIL')], [row()]), { enforceGates: false });
  assert.equal(comparison.strata.provider[0].key, 'sqlite');
  assert.equal(comparison.strata.sourceType[0].key, 'torznab');
  assert.equal(comparison.strata.clientType[0].key, 'qbittorrent');
  assert.equal(comparison.strata.cacheState[0].key, 'cold');
  assert.equal(comparison.strata.provider[0].passedPercentDelta, null);
});

test('requires treatment declarations and rejects unsupported treatment fields', () => {
  assert.throws(() => compareRuns(...runs([row()], [row()], { treatment: undefined })), /treatment/);
  assert.throws(() => compareRuns(...runs([row()], [row()], { treatment: { featureFlags: [], sharedSettings: { language: 'fr' } } })), /treatment/);
});

test('keeps unmeasured numeric deltas null', () => {
  const comparison = compareRuns(...runs([row('PASS', { outboundAttempts: null })], [row('PASS', { outboundAttempts: null })]), { enforceGates: false });
  assert.equal(comparison.cases[0].outboundAttemptsDelta, null);
  assert.equal(comparison.routes[0].outboundAttemptsDelta, null);
});

test('unknown routes fail and do not inflate passing totals', () => {
  const bad = row('PASS', { routeId: 'R99' });
  const comparison = compareRuns(...runs([bad], [bad]), { enforceGates: false });
  assert.equal(comparison.cases[0].candidateStatus, 'FAIL');
  assert.equal(comparison.totals.candidatePassed, 0);
});

test('malformed stratum values produce a failed case without crashing aggregation', () => {
  const bad = row('PASS', { provider: null });
  const comparison = compareRuns(...runs([bad], [bad]), { enforceGates: false });
  assert.equal(comparison.cases[0].candidateStatus, 'FAIL');
  assert.equal(comparison.strata.provider[0].key, 'unknown');
});

test('CLI gates are enabled unless explicitly disabled', () => {
  assert.equal(gatesEnabled(['compare']), true);
  assert.equal(gatesEnabled(['compare', '--no-gates']), false);
});

test('comparison rows retain release identity and unmeasured release metrics', () => {
  const comparison = compareRuns(...runs([row()], [row('PASS', { releaseMetrics: { recall: null, precision: null } })]), { enforceGates: false });
  assert.deepEqual(comparison.cases[0].candidateExpectedReleaseIds, ['release-1']);
  assert.deepEqual(comparison.cases[0].candidateSelectedReleaseIds, ['release-1']);
  assert.equal(comparison.cases[0].candidateReleaseRecall, null);
});
