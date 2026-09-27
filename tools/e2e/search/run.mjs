#!/usr/bin/env node
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { getScenario } from './scenarios.mjs';
import { sabConfig } from './sab.mjs';

const sourceDirectory = dirname(fileURLToPath(import.meta.url));
export function runWorkerProcess(command, args, signal) {
  return new Promise(resolveResult => {
    let stdout = '';
    let stderr = '';
    let failure;
    const child = spawn(command, args, { signal, timeout: 300000, stdio: ['ignore', 'pipe', 'pipe'] });
    child.stdout.on('data', chunk => { stdout = (stdout + chunk).slice(-1024 * 1024); });
    child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-1024 * 1024); });
    child.on('error', error => { failure = error.message; });
    child.on('close', (status, exitSignal) => resolveResult({ status, stdout, stderr, error: failure,
      signal: exitSignal, interrupted: signal.aborted }));
  });
}

export function failureKind(error, { interrupted = false, phase = 'setup' } = {}) {
  if (interrupted) return 'interrupted';
  return error?.failureCategory ?? (phase === 'acquisition' && error?.code === 'ERR_ASSERTION' ? 'acquisition' : 'infrastructure');
}

export function combineCleanupFailure(failure, category, cleanupFailed) {
  if (!cleanupFailed || failure) return { failure, category };
  return { failure: new Error('isolated resource cleanup failed'), category: 'cleanup' };
}

export function validateRig(config) {
  assert.ok(['qbittorrent', 'sabnzbd'].includes(config.clientType ?? 'qbittorrent'));
  if (config.clientType === 'sabnzbd') assert.match(config.images.sab, /^sha256:[a-f0-9]{64}$/);
  assert.match(config.buildSha, /^[a-f0-9]{40}$/);
  assert.match(config.mediaHostBase, /^\/mnt\/user\/data\/e2e-lib\/search-validation(?:\/[a-z0-9-]+)*$/);
  for (const name of ['app', 'node', 'qbit', 'postgres']) assert.match(config.images[name], /^sha256:[a-f0-9]{64}$/);
  for (const name of ['publishArchive', 'uiArchive', 'configXml', 'payloadFile', 'buildEvidenceFile']) assert.equal(resolve(config[name]), config[name]);
  for (const name of ['publishArchive', 'uiArchive', 'configXml', 'payloadFile', 'buildEvidenceFile']) assert.match(config.expectedHashes[name], /^[a-f0-9]{64}$/);
}

export async function runRig({ config, scenario, provider, output }) {
  validateRig(config);
  assert.ok(['sqlite', 'postgres'].includes(provider));
  const journey = getScenario(scenario);
  if (journey.browser) {
    assert.match(config.images.browser, /^sha256:[a-f0-9]{64}$/);
    assert.equal(resolve(config.playwrightArchive), config.playwrightArchive);
    assert.match(config.expectedHashes.playwrightArchive, /^[a-f0-9]{64}$/);
  }
  await mkdir(output, { recursive: false });
  const runId = `sv-${randomBytes(6).toString('hex')}`;
  const apiKey = randomBytes(24).toString('hex');
  const containers = [];
  const commands = [];
  let networkCreated = false;
  const mediaVerification = {};
  let failure;
  let failureCategory;
  let phase = 'setup';
  let interrupted;
  const workerAbort = new AbortController();
  const onInterrupt = () => { interrupted = true; workerAbort.abort(); };
  process.on('SIGINT', onInterrupt);
  process.on('SIGTERM', onInterrupt);
  const save = (name, data) => writeFile(resolve(output, name), typeof data === 'string' ? data : JSON.stringify(data, null, 2));
  function docker(args, { input, allowFailure = false, timeout = 240000 } = {}) {
    if (interrupted && !allowFailure) throw new Error('the isolated run was interrupted');
    const result = spawnSync('docker', args, { input, encoding: 'utf8', timeout, maxBuffer: 32 * 1024 * 1024 });
    commands.push({ args: args.map(value => value.replace(apiKey, '[isolated-key]')),
      status: result.status, error: result.error?.message, stderr: result.stderr });
    if (!allowFailure && result.status !== 0) throw new Error(`docker ${args[0]} failed (${result.status}): ${result.stderr ?? result.error}`);
    return result;
  }
  function container(kind, image, extras = [], command = ['sleep', '7200']) {
    const name = `${runId}-${kind}`;
    docker(['run', '--rm', '-d', '--name', name, '--label', `sportarr.search-validation=${runId}`, '--network', runId,
      '--network-alias', kind === 'app' ? 'sv-b0-app' : kind === 'qbit' ? 'sv-b0-qbit' : kind === 'fixture' ? 'sv-b0-fixture' : kind === 'sab' ? 'sv-b0-sab' : kind === 'browser' ? 'sv-browser' : 'sv-postgres',
      '--cpus', kind === 'app' ? '2' : ['qbit', 'sab'].includes(kind) ? '0.75' : kind === 'fixture' ? '0.25' : '0.5',
      '--memory', kind === 'app' ? '2g' : '1g',
      ...extras, '--entrypoint', command[0], image, ...command.slice(1)]);
    containers.push(name);
    return name;
  }
  const app = `${runId}-app`;
  const postgres = `${runId}-postgres`;
  try {
    for (const image of Object.values(config.images)) docker(['image', 'inspect', image]);
    const archiveHashes = {};
    for (const field of ['publishArchive', 'uiArchive', 'configXml', 'payloadFile', 'buildEvidenceFile', ...(journey.browser ? ['playwrightArchive'] : [])]) {
      archiveHashes[field] = createHash('sha256').update(await readFile(config[field])).digest('hex');
      assert.equal(archiveHashes[field], config.expectedHashes[field], `${field} differs from the frozen input`);
    }
    const buildEvidence = JSON.parse(await readFile(config.buildEvidenceFile, 'utf8'));
    assert.equal(buildEvidence.sourceSha, config.buildSha, 'the claimed build must match captured build evidence');
    assert.equal(buildEvidence.hashes['publish.tar.gz'], archiveHashes.publishArchive, 'the binary archive must match captured build evidence');
    await save('build-evidence.json', buildEvidence);
    const runnerFiles = {};
    const runnerDirectory = resolve(output, 'runner');
    await mkdir(runnerDirectory);
    for (const name of (await readdir(sourceDirectory)).filter(name => /\.(mjs|json)$/.test(name)).sort()) {
      const contents = await readFile(resolve(sourceDirectory, name));
      runnerFiles[name] = createHash('sha256').update(contents).digest('hex');
      await writeFile(resolve(runnerDirectory, name), contents);
    }
    const runnerHash = createHash('sha256').update(JSON.stringify(runnerFiles)).digest('hex');
    assert.ok((await readFile(config.payloadFile)).length <= 16 * 1024 * 1024, 'smoke payload exceeds its retained evidence budget');
    await save('inputs.json', { runId, scenario, provider, config, archiveHashes, runnerFiles, runnerHash,
      runtimeMode: 'Direct dotnet process as root. Image entrypoint and PUID/PGID setup are not exercised.',
      startupAssumption: 'For Postgres, wait ten seconds after its container starts before launching the app. For both providers, wait ten seconds after remaining setup before the worker makes its first request. Fixed waits do not prove readiness. No startup polling is used.',
      retainedMedia: { path: `${config.mediaHostBase}/${runId}`, reason: 'Retain the generated payload and imported bytes as baseline evidence.' },
      scope: 'Synthetic acquisition smoke. This is not full benchmark or route coverage.' });
    docker(['network', 'create', '--internal', '--label', `sportarr.search-validation=${runId}`, runId]);
    networkCreated = true;
    const mount = `${config.mediaHostBase}/${runId}:/data/e2e-lib`;
    if (provider === 'postgres') {
      const pg = container('postgres', config.images.postgres,
        ['-e', 'POSTGRES_PASSWORD=isolated-fixture-password', '-e', 'POSTGRES_USER=fixture', '-e', 'POSTGRES_DB=fixture'],
        ['docker-entrypoint.sh', 'postgres']);
      await save('postgres-container.json', JSON.parse(docker(['inspect', pg]).stdout));
      await delay(10000);
      const startupLog = docker(['logs', pg], { allowFailure: true });
      await save('postgres-startup.log', (startupLog.stdout ?? '') + (startupLog.stderr ?? ''));
    }
    const environment = ['-e', 'Sportarr__DataPath=/config'];
    if (provider === 'postgres') environment.push('-e', 'Sportarr__Database__Provider=postgres',
      '-e', 'Sportarr__Database__Host=sv-postgres', '-e', 'Sportarr__Database__Name=fixture',
      '-e', 'Sportarr__Database__Username=fixture', '-e', 'Sportarr__Database__Password=isolated-fixture-password');
    container('app', config.images.app, [...environment, '-v', mount]);
    docker(['exec', app, 'mkdir', '-p', '/baseline/wwwroot', '/config', '/data/e2e-lib/library', '/data/e2e-lib/downloads', '/data/e2e-lib/payload']);
    docker(['cp', config.publishArchive, `${app}:/tmp/publish.tar.gz`]);
    docker(['cp', config.uiArchive, `${app}:/tmp/ui.tar.gz`]);
    const configXml = await readFile(config.configXml, 'utf8');
    assert.ok(configXml.includes('<ApiKey>search-validation-isolated-key</ApiKey>'), 'only the isolated fixture config is accepted');
    docker(['exec', '-i', app, 'sh', '-c', 'cat > /config/config.xml'], {
      input: configXml.replace('<ApiKey>search-validation-isolated-key</ApiKey>', `<ApiKey>${apiKey}</ApiKey>`),
    });
    docker(['exec', app, 'tar', 'xzf', '/tmp/publish.tar.gz', '-C', '/baseline']);
    docker(['exec', app, 'tar', 'xzf', '/tmp/ui.tar.gz', '-C', '/baseline/wwwroot']);
    docker(['exec', '-d', '-w', '/baseline', app, 'sh', '-c', 'dotnet Sportarr.dll > /config/app.log 2>&1']);
    let qbit;
    let sab;
    if (config.clientType === 'sabnzbd') {
      sab = container('sab', config.images.sab, ['-v', `${config.mediaHostBase}/${runId}/downloads:/data/e2e-lib/downloads`]);
      docker(['exec', sab, 'mkdir', '-p', '/scratch']);
      docker(['exec', '-i', sab, 'sh', '-c', 'cat > /scratch/sabnzbd.ini'], { input: sabConfig() });
      docker(['exec', '-d', sab, 'sh', '-c', '/usr/lib/sabnzbd/venv/bin/python /usr/lib/sabnzbd/SABnzbd.py --config-file /scratch/sabnzbd.ini --server 0.0.0.0:8080 > /scratch/sab.log 2>&1']);
    } else {
    qbit = container('qbit', config.images.qbit, ['-v', `${config.mediaHostBase}/${runId}/downloads:/data/e2e-lib/downloads`]);
    docker(['exec', qbit, 'mkdir', '-p', '/scratch/qBittorrent/config']);
    const qbitConfig = `[LegalNotice]\nAccepted=true\n[Preferences]\nWebUI\\Address=*\nWebUI\\Port=8080\nWebUI\\AuthSubnetWhitelist=0.0.0.0/0\nWebUI\\AuthSubnetWhitelistEnabled=true\nWebUI\\CSRFProtection=false\nWebUI\\HostHeaderValidation=false\nWebUI\\LocalHostAuth=false\n[BitTorrent]\nSession\\DefaultSavePath=/data/e2e-lib/downloads\nSession\\DHTEnabled=false\nSession\\LSDEnabled=false\nSession\\PeXEnabled=false\n`;
    docker(['exec', '-i', qbit, 'sh', '-c', 'cat > /scratch/qBittorrent/config/qBittorrent.conf'], { input: qbitConfig });
    docker(['exec', '-d', qbit, 'sh', '-c', 'qbittorrent-nox --profile=/scratch --webui-port=8080 > /scratch/qbit.log 2>&1']);
    }
    const fixture = container('fixture', config.images.node, [
      '-v', `${config.mediaHostBase}/${runId}/payload:/data/e2e-lib/payload`,
      '-v', `${config.mediaHostBase}/${runId}/library:/data/e2e-lib/library:ro`,
      '-v', `${config.mediaHostBase}/${runId}/downloads:/data/e2e-lib/downloads:ro`]);
    docker(['cp', runnerDirectory, `${fixture}:/search`]);
    docker(['cp', config.payloadFile, `${app}:/data/e2e-lib/payload/fixture.mkv`]);
    let browser;
    if (journey.browser) {
      browser = container('browser', config.images.browser);
      docker(['exec', browser, 'mkdir', '-p', '/work/node_modules']);
      docker(['cp', config.playwrightArchive, `${browser}:/tmp/playwright.tar.gz`]);
      docker(['exec', browser, 'tar', 'xzf', '/tmp/playwright.tar.gz', '-C', '/work/node_modules']);
      docker(['cp', runnerDirectory, `${browser}:/search`]);
      docker(['exec', '-d', browser, 'sh', '-c', 'node /search/browser.mjs > /work/browser.log 2>&1']);
    }
    await delay(10000);
    await save('runtime.json', JSON.parse(docker(['inspect', ...containers]).stdout));
    const workerArgs = ['exec', '-e', `SEARCH_FIXTURE_API_KEY=${apiKey}`, fixture,
      'node', '/search/smoke.mjs', '--scenario', scenario, '--client', config.clientType ?? 'qbittorrent', '--output', '/case'];
    const execution = await runWorkerProcess('docker', workerArgs, workerAbort.signal);
    commands.push({ args: workerArgs.map(value => value.replace(apiKey, '[isolated-key]')), ...execution });
    await save('worker-exit.json', execution);
    if (browser) {
      docker(['cp', `${browser}:/case`, resolve(output, 'browser')], { allowFailure: true });
      await save('browser.log', docker(['exec', browser, 'cat', '/work/browser.log'], { allowFailure: true }).stdout);
    }
    docker(['cp', `${fixture}:/case`, resolve(output, 'case')], { allowFailure: true });
    await save('app.log', docker(['exec', app, 'cat', '/config/app.log'], { allowFailure: true }).stdout);
    if (sab) await save('sab.log', docker(['exec', sab, 'cat', '/scratch/sab.log'], { allowFailure: true }).stdout);
    if (qbit) await save('qbit.log', docker(['exec', qbit, 'cat', '/scratch/qbit.log'], { allowFailure: true }).stdout
      .replace(/(temporary password[^:]*:)[^\n]*/gi, '$1 [redacted]'));
    let workerDetail;
    try { workerDetail = JSON.parse(await readFile(resolve(output, 'case/failure.json'), 'utf8')); } catch {}
    const workerFailure = execution.status !== 0 ? Object.assign(new Error(`acquisition worker failed: ${execution.stderr || execution.error || execution.signal || 'unknown worker failure'}`),
      { failureCategory: workerDetail?.failureCategory ?? 'infrastructure' }) : null;
    const dbRead = provider === 'sqlite'
      ? docker(['exec', app, 'sqlite3', '-json', '/config/sportarr.db', 'SELECT * FROM EventFiles;'], { allowFailure: true })
      : docker(['exec', `${runId}-postgres`, 'psql', '-U', 'fixture', '-d', 'fixture', '-Atc',
        'SELECT coalesce(json_agg(t),\'[]\'::json) FROM "EventFiles" t;'], { allowFailure: true });
    await save('database-read.json', { status: dbRead.status, stderr: dbRead.stderr });
    if (dbRead.status !== 0) throw workerFailure ?? new Error('isolated database evidence read failed');
    await save('database-files.json', dbRead.stdout || '[]');
    phase = 'acquisition';
    if (workerFailure && !await readFile(resolve(output, 'case/transfer.json')).then(() => true, () => false)) throw workerFailure;
    const transfer = JSON.parse(await readFile(resolve(output, 'case/transfer.json'), 'utf8'));
    assert.ok(transfer.filePath.startsWith('/data/e2e-lib/library/'));
    const databaseFiles = JSON.parse(await readFile(resolve(output, 'database-files.json'), 'utf8'));
    assert.equal(databaseFiles.length, 1, 'the database must contain exactly one imported file');
    assert.equal(databaseFiles[0].EventId, transfer.eventId);
    assert.equal(databaseFiles[0].FilePath, transfer.filePath);
    assert.equal(databaseFiles[0].Size, transfer.bytes);
    const probing = docker(['exec', app, 'ffprobe', '-v', 'error', '-show_format', '-show_streams', '-of', 'json', transfer.filePath], { allowFailure: true });
    await save('probe-command.json', { status: probing.status, stderr: probing.stderr });
    await save('probe.json', probing.stdout);
    if ([126, 127].includes(probing.status) || /executable file not found|exec:.*no such file/i.test(probing.stderr ?? '')) throw new Error('isolated media probe executable is unavailable');
    assert.equal(probing.status, 0, 'media probe must succeed');
    const probe = JSON.parse(await readFile(resolve(output, 'probe.json'), 'utf8'));
    assert.ok(probe.streams.some(stream => stream.codec_type === 'video' && stream.width === 1280 && stream.height === 720));
    assert.ok(Number(probe.format.duration) >= 3);
    const decode = docker(['exec', app, 'ffmpeg', '-v', 'error', '-xerror', '-threads', '1', '-i', transfer.filePath, '-f', 'null', '-'], { allowFailure: true });
    await save('decode.json', { status: decode.status, stderr: decode.stderr });
    if ([126, 127].includes(decode.status) || /executable file not found|exec:.*no such file/i.test(decode.stderr ?? '')) throw new Error('isolated media decoder executable is unavailable');
    assert.equal(decode.status, 0, 'full media decode must succeed');
    Object.assign(mediaVerification, { fileHash: transfer.actualHash, mediaProbe: true, mediaDecode: true });
    if (workerFailure) throw workerFailure;
    await save('smoke-outcome.json', { status: 'PASS', scope: 'Synthetic API acquisition smoke only', provider, scenario,
      fileHash: transfer.actualHash, mediaProbe: true, mediaDecode: true, fullMatrix: 'INCOMPLETE', browser: journey.browser ? 'EXECUTED' : 'NOT_RUN' });
  } catch (error) {
    failure = error;
    failureCategory = failureKind(error, { interrupted, phase });
    await save('smoke-outcome.json', { status: 'FAIL', failureCategory, ...mediaVerification, provider, scenario, message: error.message, fullMatrix: 'INCOMPLETE' });
  } finally {
    const cleanup = [];
    if (containers.includes(app)) {
      const lastLog = docker(['exec', app, 'cat', '/config/app.log'], { allowFailure: true });
      await save('app-final.log', lastLog.stdout ?? lastLog.stderr ?? 'No app log available');
    }
    if (containers.includes(postgres)) {
      const lastLog = docker(['logs', postgres], { allowFailure: true });
      await save('postgres-final.log', (lastLog.stdout ?? '') + (lastLog.stderr ?? ''));
    }
    const discovered = docker(['ps', '-aq', '--filter', `label=sportarr.search-validation=${runId}`], { allowFailure: true });
    const cleanupNames = discovered.status === 0 ? discovered.stdout.trim().split(/\s+/).filter(Boolean) : [...containers].reverse();
    for (const name of cleanupNames) {
      const stopped = docker(['stop', '--timeout', '2', name], { allowFailure: true });
      if (stopped.status !== 0 && docker(['inspect', name], { allowFailure: true }).status !== 0) stopped.status = 0;
      cleanup.push({ name, ...stopped });
    }
    const networks = docker(['network', 'ls', '--no-trunc', '-q', '--filter', `label=sportarr.search-validation=${runId}`], { allowFailure: true });
    const networkIds = networks.status === 0 ? networks.stdout.trim().split(/\s+/).filter(Boolean) : [];
    if (networkCreated) {
      const known = docker(['network', 'inspect', '--format', '{{.Id}}', runId], { allowFailure: true });
      if (known.status === 0 && !networkIds.includes(known.stdout.trim())) networkIds.push(known.stdout.trim());
    }
    if (networks.status !== 0) cleanup.push({ operation: 'network discovery', status: networks.status, stderr: networks.stderr });
    for (const id of networkIds) cleanup.push({ network: id, ...docker(['network', 'rm', id], { allowFailure: true }) });
    await save('cleanup.json', cleanup);
    await save('commands.json', commands);
    const cleanupFailed = cleanup.some(item => item.status !== 0);
    ({ failure, category: failureCategory } = combineCleanupFailure(failure, failureCategory, cleanupFailed));
    if (failure) await save('smoke-outcome.json', { status: 'FAIL', failureCategory, cleanupFailed, ...mediaVerification, provider, scenario, message: failure.message, fullMatrix: 'INCOMPLETE' });
    process.off('SIGINT', onInterrupt);
    process.off('SIGTERM', onInterrupt);
  }
  if (failure) throw failure;
  return 0;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const args = process.argv.slice(2);
  const value = flag => { const index = args.indexOf(flag); assert.ok(index >= 0 && args[index + 1], `${flag} is required`); return args[index + 1]; };
  try {
    const config = JSON.parse(await readFile(value('--config'), 'utf8'));
    await runRig({ config, scenario: value('--scenario'), provider: value('--provider'), output: resolve(value('--output')) });
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
