import assert from 'node:assert/strict';
import { readFile, realpath, stat } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { resolve, sep } from 'node:path';

export const sabOrigin = 'http://sv-b0-sab:8080';
export const sabKey = 'isolated-sab-fixture-key';
export function sabConfig() {
  return `[misc]\nhost = 0.0.0.0\nport = 8080\napi_key = ${sabKey}\nnzb_key = isolated-nzb-fixture-key\nhost_whitelist = sv-b0-sab\ninet_exposure = 4\ndownload_dir = /data/e2e-lib/downloads/incomplete\ncomplete_dir = /data/e2e-lib/downloads/complete\ndownload_free = 0\ncomplete_free = 0\ncheck_new_rel = 0\nrefresh_rate = 1\n[servers]\n[[fixture]]\nhost = sv-b0-fixture\nport = 1190\nconnections = 2\nssl = 0\nenable = 1\noptional = 1\n[categories]\n[[sv-fixture]]\nname = sv-fixture\npp = 0\nscript = None\ndir = sv-fixture\n`;
}
export async function sabJobs(request) {
  const result = await request(`/api?mode=history&output=json&apikey=${sabKey}`, undefined, sabOrigin);
  assert.ok(Array.isArray(result.history?.slots), 'SAB history must contain slots');
  return result.history.slots;
}

const retryableQueueStatuses = new Set([0, 1, 2, 3, 5, 6, 8, 9]);
const activeQueueStatuses = new Set([...retryableQueueStatuses, 7]);
const knownQueueStatuses = new Set([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);

export function isRetryableSabTransferError(error) {
  return error?.code === 'SAB_TRANSFER_PENDING' && error.retryable === true;
}

function transferPendingError(queue) {
  const error = new Error(`SAB transfer is still moving through the app queue at status ${queue.status}`);
  error.code = 'SAB_TRANSFER_PENDING';
  error.retryable = true;
  return error;
}

function importedPathOrPending({ queue, importedFile, root }, sourceError) {
  if (retryableQueueStatuses.has(queue.status)) throw transferPendingError(queue);
  if (!importedFile) {
    if (activeQueueStatuses.has(queue.status)) throw transferPendingError(queue);
    throw sourceError;
  }
  assert.equal(queue.status, 7);
  assert.equal(importedFile.eventId, queue.eventId);
  assert.ok(importedFile.filePath.startsWith(`${root}/library/`));
  assert.ok(!importedFile.filePath.split('/').includes('..'));
  return importedFile.filePath;
}

export async function readContainedFile(path, allowedRoot) {
  const lexicalBase = resolve(allowedRoot);
  const lexicalPath = resolve(path);
  assert.ok(lexicalPath.startsWith(`${lexicalBase}${sep}`), 'file must remain inside its approved root');
  const canonicalBase = await realpath(lexicalBase);
  assert.equal(canonicalBase, lexicalBase, 'approved root must not be a symlink');
  const canonicalPath = await realpath(lexicalPath);
  assert.ok(canonicalPath.startsWith(`${canonicalBase}${sep}`), 'file must remain inside its approved root');
  const details = await stat(canonicalPath);
  assert.ok(details.isFile(), 'verified path must be a regular file');
  return readFile(canonicalPath);
}

export async function verifySabTransfer(jobs, { root, name, data, queue, importedFile }) {
  assert.equal(jobs.length, 1, 'SAB must contain exactly one completed job');
  assert.ok(queue, 'the app must track the SAB job');
  const job = jobs[0];
  assert.equal(job.status, 'Completed');
  assert.equal(job.nzo_id, queue.downloadId, 'SAB and the app must identify the same job');
  assert.ok(knownQueueStatuses.has(queue.status), 'the app queue status must be recognized');
  assert.notEqual(queue.status, 4, 'the app queue must not be failed');
  assert.ok(Number(job.bytes) >= data.length, 'SAB must report actual downloaded bytes');
  if (job.storage === '' && activeQueueStatuses.has(queue.status)) throw transferPendingError(queue);
  assert.ok(typeof job.storage === 'string' && job.storage.length > 0, 'SAB storage must be a nonempty path');
  assert.ok(job.storage.startsWith(`${root}/downloads/complete/`));
  assert.ok(!job.storage.split('/').includes('..'));
  let path;
  let verificationLocation = 'client output';
  try { path = (await stat(job.storage)).isFile() ? job.storage : `${job.storage}/${name}`; }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    path = importedPathOrPending({ queue, importedFile, root }, error);
    verificationLocation = 'imported library after source move';
  }
  assert.equal(path.split('/').at(-1), name);
  let bytes;
  try {
    const allowedRoot = verificationLocation === 'client output' ? `${root}/downloads/complete` : `${root}/library`;
    bytes = await readContainedFile(path, allowedRoot);
  }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    if (verificationLocation === 'client output') {
      path = importedPathOrPending({ queue, importedFile, root }, error);
      verificationLocation = 'imported library after source move';
      assert.equal(path.split('/').at(-1), name);
      try { bytes = await readContainedFile(path, `${root}/library`); }
      catch (importError) {
        if (importError.code === 'ENOENT' && activeQueueStatuses.has(queue.status)) throw transferPendingError(queue);
        throw importError;
      }
    } else if (activeQueueStatuses.has(queue.status)) throw transferPendingError(queue);
    else throw error;
  }
  const sha256 = value => createHash('sha256').update(value).digest('hex');
  assert.equal(sha256(bytes), sha256(data), 'SAB output must match the source bytes');
  return { clientType: 'sabnzbd', jobId: job.nzo_id, downloaded: Number(job.bytes), completed: bytes.length,
    filePath: path, originalStorage: job.storage, verificationLocation, fileHash: sha256(bytes), status: job.status };
}
