import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { isRetryableSabTransferError, readContainedFile, sabJobs, verifySabTransfer } from './sab.mjs';

test('rejects malformed history and incomplete or mismatched jobs', async () => {
  await assert.rejects(sabJobs(async () => ({})));
  const input = { root: '/data/e2e-lib', name: 'fixture.mkv', data: Buffer.from('media'), queue: { downloadId: 'one' } };
  await assert.rejects(verifySabTransfer([], input));
  await assert.rejects(verifySabTransfer([{ status: 'Downloading' }], input));
  await assert.rejects(verifySabTransfer([{ status: 'Completed', nzo_id: 'two' }], input));
});

test('requires exact source bytes in the actual SAB output', async () => {
  const root = await mkdtemp(`${tmpdir()}/sab-proof-`);
  try {
    const storage = `${root}/downloads/complete/job`;
    await mkdir(storage, { recursive: true });
    const data = Buffer.from('media');
    const input = { root, name: 'fixture.mkv', data, queue: { downloadId: 'one', status: 3 } };
    const jobs = [{ status: 'Completed', nzo_id: 'one', bytes: 6, storage }];
    await writeFile(`${storage}/fixture.mkv`, 'wrong');
    await assert.rejects(verifySabTransfer(jobs, input), /source bytes/);
    await writeFile(`${storage}/fixture.mkv`, data);
    assert.equal((await verifySabTransfer(jobs, input)).completed, 5);
    assert.equal((await verifySabTransfer([{ ...jobs[0], storage: `${storage}/fixture.mkv` }], input)).completed, 5);
    await assert.rejects(verifySabTransfer([{ ...jobs[0], bytes: 0 }], input));
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('a moved SAB payload requires an imported queue and matching event identity', async () => {
  const root = await mkdtemp(`${tmpdir()}/sab-move-proof-`);
  try {
    await mkdir(`${root}/library`);
    const data = Buffer.from('media');
    const filePath = `${root}/library/fixture.mkv`;
    await writeFile(filePath, data);
    const jobs = [{ status: 'Completed', nzo_id: 'one', bytes: 6, storage: `${root}/downloads/complete/job/fixture.mkv` }];
    const input = { root, name: 'fixture.mkv', data, queue: { downloadId: 'one', eventId: 1, status: 7 }, importedFile: { eventId: 1, filePath } };
    assert.equal((await verifySabTransfer(jobs, input)).verificationLocation, 'imported library after source move');
    await assert.rejects(verifySabTransfer(jobs, { ...input, importedFile: undefined }), error => {
      assert.equal(error.code, 'SAB_TRANSFER_PENDING');
      assert.equal(isRetryableSabTransferError(error), true);
      return true;
    });
    await assert.rejects(verifySabTransfer(jobs, { ...input, importedFile: { eventId: 2, filePath } }));
    await assert.rejects(verifySabTransfer(jobs, { ...input, queue: { ...input.queue, status: 3 } }), error => {
      assert.equal(error.code, 'SAB_TRANSFER_PENDING');
      assert.equal(isRetryableSabTransferError(error), true);
      return true;
    });
    await assert.rejects(verifySabTransfer(jobs, { ...input, queue: { ...input.queue, status: 4 } }), error => {
      assert.equal(isRetryableSabTransferError(error), false);
      return true;
    });
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('a source file disappearing after storage inspection falls back to imported evidence', async () => {
  const root = await mkdtemp(`${tmpdir()}/sab-read-race-proof-`);
  try {
    const storage = `${root}/downloads/complete/job`;
    const filePath = `${root}/library/fixture.mkv`;
    await mkdir(storage, { recursive: true });
    await mkdir(`${root}/library`, { recursive: true });
    const data = Buffer.from('media');
    await writeFile(filePath, data);
    const jobs = [{ status: 'Completed', nzo_id: 'one', bytes: 6, storage }];
    const input = {
      root,
      name: 'fixture.mkv',
      data,
      queue: { downloadId: 'one', eventId: 1, status: 7 },
      importedFile: { eventId: 1, filePath },
    };
    assert.equal((await verifySabTransfer(jobs, input)).verificationLocation, 'imported library after source move');
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('completed SAB history with empty storage remains pending during an early app queue snapshot', async () => {
  const data = Buffer.from('media');
  const jobs = [{
    status: 'Completed',
    nzo_id: 'captured-job-id',
    bytes: 1818987,
    downloaded: 1818987,
    storage: '',
    path: '/data/e2e-lib/downloads/incomplete/captured-release',
    action_line: 'Moving: fixture.mkv',
  }];
  const input = {
    root: '/data/e2e-lib',
    name: 'fixture.mkv',
    data,
    queue: { downloadId: 'captured-job-id', eventId: 1, status: 0 },
  };
  await assert.rejects(verifySabTransfer(jobs, input), error => {
    assert.equal(error.code, 'SAB_TRANSFER_PENDING');
    assert.equal(isRetryableSabTransferError(error), true);
    return true;
  });
  await assert.rejects(verifySabTransfer(jobs, { ...input, queue: { ...input.queue, status: 4 } }), error => {
    assert.equal(isRetryableSabTransferError(error), false);
    return true;
  });
  await assert.rejects(verifySabTransfer([{ ...jobs[0], storage: '/data/shared/fixture.mkv' }], input), error => {
    assert.equal(isRetryableSabTransferError(error), false);
    return true;
  });
});

test('SAB verification rejects source and imported symlinks that escape approved subtrees', async () => {
  const base = await mkdtemp(`${tmpdir()}/sab-containment-proof-`);
  const root = `${base}/isolated`;
  const shared = `${base}/shared`;
  try {
    await mkdir(`${root}/downloads/complete`, { recursive: true });
    await mkdir(`${root}/library`, { recursive: true });
    await mkdir(shared);
    await writeFile(`${shared}/fixture.mkv`, 'media');
    await symlink(shared, `${root}/downloads/complete/job`);
    const jobs = [{ status: 'Completed', nzo_id: 'one', bytes: 5, storage: `${root}/downloads/complete/job` }];
    const queue = { downloadId: 'one', eventId: 1, status: 7 };
    await assert.rejects(verifySabTransfer(jobs, { root, name: 'fixture.mkv', data: Buffer.from('media'), queue }));

    await rm(`${root}/downloads/complete/job`);
    await symlink(`${shared}/fixture.mkv`, `${root}/library/fixture.mkv`);
    const missingJobs = [{ ...jobs[0], storage: `${root}/downloads/complete/missing` }];
    await assert.rejects(verifySabTransfer(missingJobs, {
      root,
      name: 'fixture.mkv',
      data: Buffer.from('media'),
      queue,
      importedFile: { eventId: 1, filePath: `${root}/library/fixture.mkv` },
    }));
  } finally { await rm(base, { recursive: true, force: true }); }
});

test('a failed app queue cannot verify from a remaining regular source file', async () => {
  const root = await mkdtemp(`${tmpdir()}/sab-failed-source-proof-`);
  try {
    const storage = `${root}/downloads/complete/job`;
    await mkdir(storage, { recursive: true });
    await writeFile(`${storage}/fixture.mkv`, 'media');
    const jobs = [{ status: 'Completed', nzo_id: 'one', bytes: 5, storage }];
    await assert.rejects(verifySabTransfer(jobs, {
      root,
      name: 'fixture.mkv',
      data: Buffer.from('media'),
      queue: { downloadId: 'one', eventId: 1, status: 4 },
    }));
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('contained file reader rejects escaped files and a symlinked approved root', async () => {
  const base = await mkdtemp(`${tmpdir()}/sab-reader-proof-`);
  try {
    const allowed = `${base}/allowed`;
    const shared = `${base}/shared`;
    await mkdir(allowed);
    await mkdir(shared);
    await writeFile(`${allowed}/valid.mkv`, 'valid');
    await writeFile(`${shared}/escaped.mkv`, 'escaped');
    assert.equal((await readContainedFile(`${allowed}/valid.mkv`, allowed)).toString(), 'valid');
    await symlink(`${shared}/escaped.mkv`, `${allowed}/escaped.mkv`);
    await assert.rejects(readContainedFile(`${allowed}/escaped.mkv`, allowed));
    const linkedRoot = `${base}/linked-root`;
    await symlink(shared, linkedRoot);
    await assert.rejects(readContainedFile(`${linkedRoot}/escaped.mkv`, linkedRoot));
  } finally { await rm(base, { recursive: true, force: true }); }
});
