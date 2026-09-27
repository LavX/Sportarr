# Search acquisition validation

This harness runs Sportarr against local Torznab or Newznab fixtures and a real qBittorrent or SABnzbd client. It serves generated media, checks client operations, waits for import, and verifies the imported bytes with SHA256, ffprobe, and a full decode.

The current scenarios are small acquisition checks. They do not prove all sports, indexers, user routes, restart behavior, or production recall. `routes.json` records the wider required route matrix. Missing cases remain incomplete.

## Run checks

Use Node 24 or later and an existing Docker daemon.

```sh
node --test tools/e2e/search/*.test.mjs
node tools/e2e/search/run.mjs --config /absolute/path/rig.json --scenario automatic --provider sqlite --output /absolute/path/new-run
node tools/e2e/search/collect.mjs --mode baseline --output /absolute/path/new-ledger --run /absolute/path/new-run
```

Output directories must be new. `scenarios.mjs` lists the supported scenario names. Providers are `sqlite` and `postgres`. Client types are `qbittorrent` and `sabnzbd`. Browser scenarios use a pinned Playwright installation and click the league page's Main Card search controls.

The configuration requires these fields.

| Field | Value |
| --- | --- |
| `buildSha` | Full source commit SHA. An uncommitted candidate also needs explicit snapshot provenance in its build evidence. |
| `publishArchive` | Absolute path to the published backend tar archive. |
| `uiArchive` | Absolute path to its built frontend tar archive. |
| `configXml` | Absolute path to an isolated configuration template. |
| `payloadFile` | Absolute path to the generated three-second 720p test video. Maximum 16 MiB. |
| `buildEvidenceFile` | Absolute path to JSON containing `sourceSha` and `hashes["publish.tar.gz"]`. Retain build commands and results separately. |
| `mediaHostBase` | Dedicated directory under `/mnt/user/data/e2e-lib/search-validation`. |
| `clientType` | `qbittorrent` or `sabnzbd`. |
| `images` | Existing Docker image IDs for `app`, `node`, `qbit`, and `postgres`. Add `sab` for Usenet and `browser` for browser scenarios. Every value must be a full `sha256:` image ID. |
| `expectedHashes` | SHA256 hashes keyed by each input file field above. |
| `playwrightArchive` | Absolute path to the pinned Playwright module archive for browser scenarios, also included in `expectedHashes`. |

The runner copies inputs into disposable containers. It creates an internal network and mounts only a new per-run media directory. It does not pull images or use production clients. It runs the app process directly as root, so image entrypoint and PUID/PGID setup are outside this check. The application configuration template must contain only isolated settings and the placeholder key `search-validation-isolated-key`.

Positive small-file scenarios explicitly set quality definition minimum sizes to zero. The generated fixture is named for one synthetic UFC Main Card. Ordinary size thresholds and other sports need separate cases. Torrent scenarios use a real webseed transfer. Usenet scenarios fetch local NNTP articles and let SABnzbd decode them. These checks do not exercise remote peer discovery or a commercial Usenet server.

## Read evidence

Each run retains input hashes, a runner source snapshot, API captures, indexer requests, client operations, application logs, database file rows, transfer hashes, and media verification results. Browser runs also retain screenshots and a trace. The media directory remains as evidence. Containers and networks created by the run are removed in cleanup.

A part mismatch fails acquisition even when the bytes imported correctly. The fixture contains Main Card only; Sportarr interprets an imported file with no part as covering the full event. The expected part comes from known fixture content, independently of the request selector. The transfer evidence remains available for diagnosis. SABnzbd may move its completed output into the library before verification. The ledger records whether the bytes were checked in client output or the imported library; its job identity and byte counter remain separate evidence.

Infrastructure and cleanup failures block verification. A cleanup problem does not erase an earlier acquisition failure. A recorded failure is a failed check and needs its raw evidence reviewed before assigning a product root cause.

Collect torrent and Usenet lanes separately because their fixture corpora differ. All runs in one bundle must use identical build archives, source snapshot, corpus, and runtime settings. Across a baseline and candidate, build archives may differ while test settings and the runner stay fixed. The baseline collector pins B0 to `029eb3e4ebb93acfd10c35784247b32f49b59c84`.

```sh
node tools/e2e/search/report.mjs compare --baseline /absolute/path/baseline-ledger --candidate /absolute/path/candidate-ledger > /absolute/path/comparison.json
```

The report's full route gate remains incomplete until the matrix is covered. `--no-gates` can produce an exploratory report; it does not certify the change. Do not infer automatic selection from a generic search approval or real-world recall from this single-release catalogue. Development runs with competing workloads cannot establish latency improvements.

## Broader retrieval corpus

`corpus.mjs` generates 320 synthetic cases across eight event shapes and 32 naming profiles. Tests pin its exact generated hash. `corpus-evaluation.mjs` projects public source inventories and grades returned offers against independent content coverage. Hidden labels and semantic content IDs must never enter the source fixture or the application's search hints.

This corpus is separate from the acquisition runner's single Main Card payload. Its listed release sizes are logical inventory data, and it contains no materialized media. Its metadata is synthetic, including participant fields that may not resemble production metadata for some sports. It cannot establish production recall. Missing automatic-decision evidence remains unmeasured, and the grader never reports a transfer pass.
