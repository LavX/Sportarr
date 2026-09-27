#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { cp, mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { basename, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { getScenario } from './scenarios.mjs';

const B0 = '029eb3e4ebb93acfd10c35784247b32f49b59c84';
const RUN_FILES = [
  'inputs.json', 'case/corpus.json', 'case/http.json', 'case/files.json',
  'case/source-requests.json', 'case/trigger.json', 'case/client.json',
  'case/queue.json', 'case/timing.json', 'case/transfer.json', 'probe.json',
  'decode.json', 'database-files.json', 'smoke-outcome.json', 'cleanup.json',
  'build-evidence.json',
];

function digest(data) {
  return createHash('sha256').update(data).digest('hex');
}

async function readJson(path, required = true) {
  try {
    const bytes = await readFile(path);
    try {
      return { value: JSON.parse(bytes), bytes, hash: digest(bytes), parseError: null };
    } catch (error) {
      return { value: null, bytes, hash: digest(bytes), parseError: error.message };
    }
  } catch (error) {
    if (!required && error.code === 'ENOENT') return null;
    throw error;
  }
}

function routeFor(scenario) {
  try {
    const { routeId, variantId, actions } = getScenario(scenario);
    return { routeId, variantId, actions };
  } catch {
    return { routeId: 'UNKNOWN', variantId: 'UNKNOWN', actions: [] };
  }
}

export async function collectRun(directory) {
  const root = resolve(directory);
  const records = new Map();
  const missing = [];
  for (const name of RUN_FILES) {
    const record = await readJson(join(root, name), false);
    if (record) records.set(name, record);
    else missing.push(name);
  }
  try {
    const bytes = await readFile(join(root, 'app.log'));
    records.set('app.log', { value: bytes.toString('utf8'), bytes, hash: digest(bytes), parseError: null });
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
  }
  for (const name of ['browser/trace.zip', 'browser/results.png', 'browser/final.png', 'browser/automatic.png', 'browser/result.json', 'browser/requests.json', 'app-final.log', 'qbit.log', 'sab.log', 'browser.log']) {
    try { const bytes = await readFile(join(root, name)); records.set(name, { value: null, bytes, hash: digest(bytes), parseError: null }); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
  const inputs = records.get('inputs.json')?.value;
  if (!inputs) throw new Error(`${root} is missing inputs.json`);
  const scenario = inputs.scenario;
  const clientType = inputs.config.clientType ?? 'qbittorrent';
  const optional = ['browser/result.json', 'browser/requests.json', 'case/push-outcome.json', 'case/payload-requests.json', 'case/client-requests.json', 'database-read.json', 'case/wait-budget.json', 'case/scheduling-control.json', 'worker-exit.json', 'commands.json', 'runtime.json', 'probe-command.json', 'case/repeat-trigger.json', 'case/failure.json', 'case/search.json', 'case/warm-cache.json', 'case/tasks.json', 'case/fixture-settings.json', 'case/client-transfer.json', 'case/nntp-source.json', 'case/nntp-requests.json', 'case/nntp-connection-errors.json'];
  for (const name of optional) { const record = await readJson(join(root, name), false); if (record) records.set(name, record); }
  const clientRequests = records.get('case/client-requests.json')?.value;
  const clientAdds = Array.isArray(clientRequests) ? clientRequests.filter(item => item.kind === 'add' && item.accepted === true).length : null;
  const clientTransfer = records.get('case/client-transfer.json')?.value;
  const route = routeFor(scenario);
  const corpus = records.get('case/corpus.json')?.value;
  const trigger = records.get('case/trigger.json')?.value;
  const eventId = trigger?.eventId ?? null;
  const need = eventId === null ? [] : [`event:${eventId}:main`];
  const outcome = records.get('smoke-outcome.json')?.value;
  const files = records.get('case/files.json')?.value ?? [];
  const requests = records.get('case/source-requests.json')?.value;
  const client = records.get('case/client.json')?.value ?? [];
  const actualClientBytes = clientType === 'sabnzbd' ? clientTransfer?.completed : client[0]?.completed;
  const downloadedBytes = clientType === 'sabnzbd' ? clientTransfer?.downloaded : client[0]?.downloaded;
  const transfer = records.get('case/transfer.json')?.value;
  const databaseFiles = records.get('database-files.json')?.value ?? [];
  const probe = records.get('probe.json')?.value;
  const decode = records.get('decode.json')?.value;
  const timing = records.get('case/timing.json')?.value;
  const http = [...(records.get('case/http.json')?.value ?? []), ...(records.get('browser/requests.json')?.value ?? [])];
  const appLog = records.get('app.log')?.value ?? '';
  let runnerProvenance = { status: 'INCOMPLETE', runnerHash: inputs.runnerHash ?? null, files: [] };
  if (inputs.runnerHash && inputs.runnerFiles && typeof inputs.runnerFiles === 'object') {
    const runnerFiles = [];
    for (const [name, expectedHash] of Object.entries(inputs.runnerFiles).sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0)) {
      const record = await readJson(join(root, 'runner', name), false);
      runnerFiles.push({ name, expectedHash, actualHash: record?.hash ?? null, valid: record?.hash === expectedHash, record });
    }
    const sortedFiles = Object.fromEntries(Object.entries(inputs.runnerFiles).sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0));
    const calculatedHash = digest(Buffer.from(JSON.stringify(sortedFiles)));
    runnerProvenance = { status: runnerFiles.every(file => file.valid) && calculatedHash === inputs.runnerHash ? 'COMPLETE' : 'INCOMPLETE', runnerHash: inputs.runnerHash, calculatedHash, files: runnerFiles };
  }
  const explicitSelected = http.filter(item => item.path === '/api/release/grab').map(item => item.body?.guid).filter(Boolean);
  const returned = [...new Set((requests ?? []).flatMap(item => item.releaseGuids ?? []))];
  const evidenceProblems = [];
  for (const [name, record] of records) {
    if (record.parseError) evidenceProblems.push(`${name} is not valid JSON`);
  }
  if (outcome?.status === 'PASS') {
    if (!transfer) evidenceProblems.push('transfer evidence is missing');
    if (!probe?.streams?.length || outcome.mediaProbe !== true) evidenceProblems.push('probe evidence is missing or failed');
    if (decode?.status !== 0 || outcome.mediaDecode !== true) evidenceProblems.push('decode evidence is missing or failed');
    if (files.length !== 1 || databaseFiles.length !== 1) evidenceProblems.push('one API and database file record is required');
    if (eventId === null || files[0]?.eventId !== eventId || databaseFiles[0]?.EventId !== eventId) evidenceProblems.push('database event identity does not match the triggered event');
    if (!transfer || transfer.expectedHash !== transfer.actualHash || transfer.actualHash !== corpus?.expectedSha256 || transfer.actualHash !== outcome.fileHash) evidenceProblems.push('payload hashes do not agree');
    if (clientType === 'sabnzbd' && (!clientTransfer || clientTransfer.jobId !== client[0]?.nzo_id || client[0]?.status !== 'Completed' || clientTransfer.fileHash !== transfer?.actualHash || Number(client[0]?.bytes) < transfer?.bytes)) evidenceProblems.push('SAB job identity or byte evidence does not match');
    if (!client.length || actualClientBytes !== transfer?.bytes || !Number.isFinite(downloadedBytes) || downloadedBytes < transfer?.bytes) evidenceProblems.push('actual client byte evidence does not match the transfer');
  }
  let status = outcome?.status === 'PASS' ? 'PASS' : 'FAIL';
  let failure = null;
  let limitation = null;
  let blockedReason = null;
  let defect = status === 'FAIL';
  if (scenario.includes('rss') && Array.isArray(requests) && requests.length === 0) {
    status = 'FAIL';
    defect = true;
    failure = scenario.startsWith('rss-task') ? 'RSS_TASK_NO_SOURCE_REQUESTS' : 'RSS_BACKGROUND_NO_SOURCE_REQUESTS';
  } else if (route.routeId === 'UNKNOWN') {
    status = 'BLOCKED';
    blockedReason = `unknown smoke scenario: ${scenario}`;
    defect = false;
  } else if (status === 'PASS' && (missing.length > 0 || evidenceProblems.length > 0)) {
    status = 'BLOCKED';
    blockedReason = [...missing.map(name => `missing ${name}`), ...evidenceProblems].join('; ');
    defect = false;
  }
  const partMeasured = transfer && Object.hasOwn(transfer, 'expectedPart') && Object.hasOwn(transfer, 'actualPart');
  const partMismatch = partMeasured && transfer.expectedPart !== transfer.actualPart;
  if (partMismatch) { status = 'FAIL'; defect = true; failure = 'IMPORTED_PART_MISMATCH'; }
  if (records.get('worker-exit.json')?.value?.interrupted === true || ['interrupted', 'cleanup', 'infrastructure'].includes(outcome?.failureCategory)) {
    status = 'BLOCKED';
    defect = false;
    failure = null;
    blockedReason = outcome?.failureCategory === 'infrastructure' ? `Test infrastructure failed: ${outcome.message ?? 'See raw evidence'}` : outcome?.failureCategory === 'cleanup' ? 'Isolated resource cleanup failed' : 'Run interrupted before verification';
  }
  const evidenceContext = [...missing.map(name => `missing ${name}`), ...evidenceProblems].join('; ') || null;
  const imported = status === 'PASS' ? need : [];
  const fileChecks = status === 'PASS' ? [{
    path: transfer.filePath, payloadId: corpus.releases[0]?.guid ?? transfer.infoHash,
    expectedHash: transfer.expectedHash, actualHash: transfer.actualHash,
    contentVerified: true, insideTestRoot: transfer.filePath.startsWith('/data/e2e-lib/'),
    probeVerified: true, decodeVerified: true, databaseEventId: databaseFiles[0].EventId,
    actualClientBytes,
  }] : [];
  return {
    root, records, inputs,
    result: {
      ...route, expectation: 'IMPORT', caseId: `smoke:${scenario}:${inputs.provider}:${basename(root)}`, runId: inputs.runId, setupVariant: basename(root), provider: inputs.provider,
      sourceType: clientType === 'sabnzbd' ? 'newznab' : 'torznab', clientType, cacheState: scenario.includes('warm') ? 'warm' : 'cold',
      buildSha: inputs.config?.buildSha, status, expectedNeeds: need,
      expectedReleaseIds: corpus?.releases?.map(release => release.guid).filter(Boolean) ?? [],
      returnedReleaseIds: returned, selectionEvidence: explicitSelected.length ? 'Observed grab API request' : status === 'PASS' ? 'Inferred from the single catalogue release and verified file' : 'Not established',
      selectedReleaseIds: explicitSelected.length > 0 ? explicitSelected : status === 'PASS' && corpus?.releases?.[0]?.guid ? [corpus.releases[0].guid] : [], importedNeeds: imported,
      expectedPartBasis: transfer?.expectedPartBasis ?? (partMeasured ? 'Known synthetic payload content' : null), expectedPart: partMeasured ? transfer.expectedPart : null, actualPart: partMeasured ? transfer.actualPart : null, partEvidence: partMeasured ? 'Measured' : 'Unmeasured',
      unexpectedAssignments: partMismatch ? [{ eventId, expectedPart: transfer.expectedPart, actualPart: transfer.actualPart }] : [], clientAdds, duplicateAdds: clientAdds === null ? null : Math.max(0, clientAdds - 1),
      outboundAttempts: Array.isArray(requests) ? requests.length : null,
      quotaViolations: Number.isInteger(corpus?.sourceQuota) && Array.isArray(requests) ? Math.max(0, requests.length - corpus.sourceQuota) : null, fileChecks, elapsedMs: Number.isFinite(timing?.elapsedMs) ? Math.round(timing.elapsedMs) : null,
      releaseMetrics: { recall: null, precision: null },
      clientByteEvidence: clientTransfer?.verificationLocation ?? (clientType === 'qbittorrent' ? 'Client completed byte counter' : null),
      cleanupFailed: outcome?.cleanupFailed === true, evidencePaths: [], actualClientBytes: actualClientBytes ?? null,
      fullMatrix: outcome?.fullMatrix ?? 'INCOMPLETE', defect, failure, limitation, blockedReason, evidenceContext, runnerProvenance: runnerProvenance.status,
    },
    runnerProvenance,
  };
}

export async function collectRuns(directories, outputDirectory, { mode = 'baseline' } = {}) {
  if (!directories.length) throw new Error('at least one --run directory is required');
  const output = resolve(outputDirectory);
  await mkdir(output);
  const collected = await Promise.all(directories.map(collectRun));
  const corpusRecord = collected.map(run => run.records.get('case/corpus.json')).find(Boolean);
  const corpusBytes = corpusRecord?.bytes;
  if (!corpusBytes || collected.some(run => run.records.get('case/corpus.json') && run.records.get('case/corpus.json').hash !== digest(corpusBytes))) throw new Error('available smoke corpora do not agree');
  await writeFile(join(output, 'corpus.json'), corpusBytes);
  const hashesFor = run => run.inputs.archiveHashes ?? run.inputs.expectedHashes ?? run.inputs.config?.expectedHashes ?? {};
  const settingsFor = run => ({
    images: Object.fromEntries(Object.entries(run.inputs.config?.images ?? {}).sort()),
    clientType: run.inputs.config?.clientType ?? 'qbittorrent',
    configXmlHash: hashesFor(run).configXml ?? null, payloadHash: hashesFor(run).payloadFile ?? null,
    runtimeMode: run.inputs.runtimeMode ?? null, startupAssumption: run.inputs.startupAssumption ?? null,
  });
  const buildFor = run => ({ publish: hashesFor(run).publishArchive ?? null, ui: hashesFor(run).uiArchive ?? null,
    source: run.records.get('build-evidence.json')?.value?.hashes?.['source.tar.gz'] ?? null });
  for (const run of collected) for (const [key, value] of Object.entries(buildFor(run))) {
    if (value !== buildFor(collected[0])[key]) throw new Error(`runs in one bundle must use identical build archives: ${key} differs in ${run.inputs.runId}`);
  }
  const shared = settingsFor(collected[0]);
  for (const run of collected) for (const [key, value] of Object.entries(settingsFor(run))) {
    if (JSON.stringify(value) !== JSON.stringify(shared[key])) throw new Error(`smoke runs do not share fixed runtime settings: ${key} differs in ${run.inputs.runId}`);
  }
  const sharedBytes = Buffer.from(`${JSON.stringify(shared, null, 2)}\n`);
  await writeFile(join(output, 'shared-settings.json'), sharedBytes);
  const ledger = { generatedAt: new Date().toISOString(), files: [], fixedInputs: [] };
  const copiedFixedInputs = new Set();
  for (const run of collected) {
    const snapshotRoot = join(output, 'evidence', run.inputs.runId);
    for (const [name, record] of run.records) {
      const destination = join(snapshotRoot, name);
      await mkdir(resolve(destination, '..'), { recursive: true });
      await cp(join(run.root, name), destination);
      const relative = `evidence/${run.inputs.runId}/${name}`;
      ledger.files.push({ runId: run.inputs.runId, path: relative, sha256: record.hash, bytes: record.bytes.length });
      run.result.evidencePaths.push({ path: relative, kind: name, exists: true });
    }
    for (const runnerFile of run.runnerProvenance.files) {
      if (!runnerFile.record) continue;
      const relative = `evidence/${run.inputs.runId}/runner/${runnerFile.name}`;
      const destination = join(output, relative);
      await mkdir(resolve(destination, '..'), { recursive: true });
      await cp(join(run.root, 'runner', runnerFile.name), destination);
      ledger.files.push({ runId: run.inputs.runId, path: relative, sha256: runnerFile.record.hash, bytes: runnerFile.record.bytes.length, kind: 'runner-source' });
      run.result.evidencePaths.push({ path: relative, kind: 'runner-source', exists: true });
    }
    for (const [key, source] of Object.entries(run.inputs.config ?? {})) {
      if (typeof source !== 'string' || !hashesFor(run)[key]) continue;
      const bytes = await readFile(source);
      const actual = digest(bytes);
      if (actual !== hashesFor(run)[key]) throw new Error(`${run.inputs.runId} fixed input ${key} hash mismatch`);
      const relative = `fixed-inputs/${actual}-${basename(source)}`;
      if (!copiedFixedInputs.has(actual)) {
        await mkdir(join(output, 'fixed-inputs'), { recursive: true });
        await cp(source, join(output, relative));
        copiedFixedInputs.add(actual);
      }
      ledger.fixedInputs.push({ runId: run.inputs.runId, key, source, snapshot: relative, sha256: actual, bytes: bytes.length });
    }
  }
  const results = collected.map(run => run.result);
  const completeProvenance = collected.every(run => run.runnerProvenance.status === 'COMPLETE');
  const commonRunner = completeProvenance && new Set(collected.map(run => run.runnerProvenance.runnerHash)).size === 1;
  const scope = commonRunner
    ? 'Verified synthetic smoke ledger with captured runner provenance. Full benchmark coverage is incomplete.'
    : 'Exploratory synthetic smoke ledger. Full benchmark coverage and early-run source provenance are incomplete.';
  const manifest = {
    schemaVersion: 1, runId: `${mode}-smoke-${digest(Buffer.from(directories.join('\n'))).slice(0, 12)}`, mode,
    build: { sha: collected[0].inputs.config.buildSha, imageDigest: collected[0].inputs.config.images.app,
      sourceState: collected[0].records.get('build-evidence.json')?.value?.sourceState ?? null,
      sourceArchiveHash: collected[0].records.get('build-evidence.json')?.value?.hashes?.['source.tar.gz'] ?? null,
      publishArchiveHash: hashesFor(collected[0]).publishArchive ?? null, uiArchiveHash: hashesFor(collected[0]).uiArchive ?? null },
    corpus: { path: 'corpus.json', hash: digest(corpusBytes) },
    sharedSettings: { path: 'shared-settings.json', hash: digest(sharedBytes) },
    runner: commonRunner ? { version: 'captured-smoke-runner', sha: collected[0].runnerProvenance.runnerHash } : { version: 'unverified-exploratory-smoke', sha: null },
    provenanceStatus: commonRunner ? 'COMPLETE' : 'INCOMPLETE', provenanceClaim: 'Captured runner and build evidence only. No embedded binary source attestation is claimed.',
    providers: [...new Set(results.map(row => row.provider))].sort(),
    expectedCases: results.map(({ routeId, variantId, caseId, provider, sourceType, clientType, cacheState }) => ({ routeId, variantId, caseId, provider, sourceType, clientType, cacheState })),
    treatment: { featureFlags: [] }, scope,
  };
  if (manifest.build.sha !== B0 && mode === 'baseline') throw new Error(`baseline build SHA must be ${B0}`);
  if (collected.some(run => run.inputs.config.buildSha !== manifest.build.sha || run.records.get('build-evidence.json')?.value?.sourceSha !== manifest.build.sha)) throw new Error('run build identity does not match build evidence');
  const summary = {
    scope: manifest.scope, fullBenchmark: 'INCOMPLETE', cases: results.length,
    smokePasses: results.filter(row => row.status === 'PASS').length,
    failures: results.filter(row => row.status === 'FAIL').map(row => ({ caseId: row.caseId, failure: row.failure })),
    blocked: results.filter(row => row.status === 'BLOCKED').map(row => ({ caseId: row.caseId, limitation: row.limitation, blockedReason: row.blockedReason })),
    unmeasured: ['clientAdds', 'duplicateAdds', 'quotaViolations'].filter(key => results.some(row => row[key] === null)),
  };
  await Promise.all([
    writeFile(join(output, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`),
    writeFile(join(output, 'results.json'), `${JSON.stringify(results, null, 2)}\n`),
    writeFile(join(output, 'evidence-ledger.json'), `${JSON.stringify(ledger, null, 2)}\n`),
    writeFile(join(output, 'summary.json'), `${JSON.stringify(summary, null, 2)}\n`),
  ]);
  return { manifest, results, ledger };
}

export async function main(argv = process.argv.slice(2)) {
  const outputIndex = argv.indexOf('--output');
  const output = outputIndex >= 0 ? argv[outputIndex + 1] : null;
  const runs = argv.flatMap((value, index) => value === '--run' && argv[index + 1] ? [argv[index + 1]] : []);
  const modeIndex = argv.indexOf('--mode');
  const mode = modeIndex >= 0 ? argv[modeIndex + 1] : 'baseline';
  if (!['baseline', 'candidate'].includes(mode)) throw new Error('--mode must be baseline or candidate');
  if (!output || !runs.length) throw new Error('usage: collect.mjs [--mode baseline|candidate] --output DIR --run DIR [--run DIR ...]');
  const result = await collectRuns(runs, output, { mode });
  process.stdout.write(`${JSON.stringify({ output: resolve(output), cases: result.results.length, fullMatrix: 'INCOMPLETE' })}\n`);
  return result.results.some(row => row.status === 'FAIL') ? 1 : result.results.some(row => row.status === 'BLOCKED') ? 2 : 0;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  main().then(code => { process.exitCode = code; }).catch(error => { process.stderr.write(`${error.message}\n`); process.exitCode = 2; });
}
