# Local Ollama Embedding Evaluation

## Objective

Implement a genuine local-model evidence boundary using Ollama embeddings, without
cloud access, automatic model downloads, or uncalibrated mutation authority. Run the
model only when explicitly configured, collect bounded reproducible similarity
observations for unresolved Candidate pairs, cache reduced pair comparisons, and
prepare a real calibration workflow. Do not change executable grouping until actual
model outputs have been reviewed on calibration and frozen holdout.

## Scope

- Add provider-neutral Domain values for local embedding model identity, input
  identity, bounded vectors, cosine comparison, coverage/status, provenance, and
  policy versions.
- Add an Application embedding port and cache port plus a bounded orchestration use
  case targeting only the ten remaining unresolved Candidate pairs.
- Build bounded metadata inputs from title, authors, and normalized language only in
  this first slice; do not send paths, identifiers, file content, or provider data.
- Add a loopback-only Ollama `/api/embed` Infrastructure adapter using configured
  model name/version, batch input, `truncate=false`, bounded dimensions/body/time,
  and no model pull/download behavior.
- Add a versioned pair-comparison cache keyed by both input hashes, model/runtime,
  preprocessing, dimensions, and comparison policy versions. Persist similarity and
  status only, not vectors or raw inputs.
- Add progress and aggregate cold/warm/unavailable metrics with explicit local-model
  configuration disclosure.
- Add fake-vector Domain/Application tests and fake-HTTP Infrastructure tests. No
  ordinary test depends on Ollama or a model.
- Add an opt-in local calibration command/test that requires a configured Ollama
  model and emits opaque scenario IDs plus aggregate calibration/holdout metrics.
- Keep local-model evidence observational until that command records real outputs and
  a follow-up threshold plan is accepted.

## Out of scope

- Installing Ollama, pulling a model, bundling model weights, choosing a threshold
  from fabricated vectors, or modifying corpus ground truth.
- Cloud Ollama endpoints or any non-loopback model endpoint.
- Sending EPUB/PDF text, page content, identifiers, paths, provider payloads, or
  credentials to the model.
- Adding executable Anchor/Strong evidence, contradictions, group changes, baseline
  quality claims, or mutation authority in this slice.
- Generation/chat APIs, prompts, agents, reranking, OCR, cover/image models, or model
  training/fine-tuning.
- Supporting multiple local runtimes before one measured Ollama path is accepted.

## Relevant requirements

- Cloud AI is not accepted; local model evidence is versioned and advisory.
- Domain receives provider-neutral values, never runtime tensors or model objects.
- Every cache key includes complete model/runtime/input/preprocessing/policy identity.
- Embeddings, metadata inputs, paths, and payloads are never logged.
- Long work is bounded, responsive, and visibly active; unavailable runtime/model
  falls back to existing matching.
- Calibration and frozen holdout evidence are required before model output can affect
  executable groups.

## Existing implementation inspected

- Candidate routing now reaches all 232 labeled positive pairs with 100% precision;
  ten synthetic pairs remain `CONTENT_UNAVAILABLE_OR_WEAK`.
- Open Library resolution demonstrates the Domain/Application/Infrastructure/cache/
  progress/provenance pattern and remains independent from mutation.
- `DiscoverWorkLanguageCandidatesUseCase` has local content then online provider
  phases before final decision/clustering; observational local model evaluation
  belongs after provider resolution and before final summary publication.
- Existing `CandidateEvidence` supports provenance, but this slice will not add model
  evidence to decisions until calibrated.
- Official Ollama documentation exposes unauthenticated local `POST /api/embed` at
  `http://localhost:11434`, supports batched string input and optional dimensions,
  and returns model identity plus vectors. The API is expected to be stable but is
  not strictly versioned.
- Ollama is not installed in the current environment, so no real model output or
  threshold can be verified in this execution.

## Proposed design

### Configuration

Local embeddings are disabled unless `CALIBRE_OLLAMA_EMBEDDING_MODEL` is a bounded
model identifier. The endpoint is fixed to `http://127.0.0.1:11434/`; arbitrary or
cloud endpoints are rejected. Optional `CALIBRE_OLLAMA_EMBEDDING_MODEL_VERSION`
records the exact locally managed model revision/digest supplied by the user. Without
an explicit version, observations are reported but not cacheable/calibration-valid.

Initial bounds: 10-second request timeout, batches of at most 8, 24 unique inputs/run,
512 UTF-16 characters/input, 128-4096 finite vector dimensions, 2 MiB response, one
active request, and no retries or automatic pulls.

### Domain

`LocalEmbeddingInputIdentity` hashes preprocessing version plus normalized title,
author, and language fields. `LocalEmbeddingVector` validates finite bounded values
and normalizes for comparison in memory. `LocalEmbeddingComparisonPolicy` computes
symmetric cosine similarity as integer permille with model/input/policy provenance.
No threshold maps similarity to Candidate evidence in this plan.

### Application

Select members of pairs still Weak after local content and bibliographic enrichment.
Deduplicate inputs by hash, read comparison cache, batch remaining inputs through the
local provider, compute pair comparisons, cache reduced results, and expose aggregate
queries/cache hits/model calls/comparisons/failures. Any runtime/cache/model error
returns unavailable observations and existing local/provider decisions continue.

### Infrastructure

Ollama adapter uses fixed loopback HTTP and strict JSON. Request contains model,
bounded input array, `truncate=false`, and configured dimensions when supplied.
Response model must match configuration, embedding count must match input count, and
all vectors must share valid dimensions/finite values. Errors/timeout/404/model-not-
found/oversize/malformed become controlled unavailable status.

Pair cache follows existing physical-path/reparse/atomic/pruning patterns. Raw inputs
and vectors are never written.

### Calibration workflow

An opt-in test/command enabled by `CALIBRE_RUN_LOCAL_MODEL_EVALUATION=1` runs the
configured model over corpus record metadata and emits only opaque scenario/record
IDs, model/runtime/preprocessing versions, integer similarities, and aggregate split
metrics. It writes only to an explicitly supplied temporary output path. A later
plan reviews distributions, chooses thresholds if justified, adds hard negatives,
and only then permits model evidence to affect decisions.

## Files expected to change

- Domain local embedding values/comparison policy and tests
- Application embedding/cache ports, options, orchestration, progress, and tests
- Infrastructure Ollama options/client/cache/DI and fake-HTTP/filesystem tests
- Candidate discovery/run-summary/WPF observational disclosure
- optional local corpus evaluation harness and documentation
- `docs/architecture.md`, `docs/functional-requirements.md`,
  `docs/duplicate-detection.md`, `docs/test-strategy.md`, `docs/roadmap.md`
- this plan and `docs/plans/README.md`

No mutation code, corpus label, or reviewed matching baseline is expected to change.

## Safety considerations

- Endpoint is fixed loopback HTTP; no cloud/non-loopback configuration is accepted.
- Model is never downloaded or started by the application.
- Only disclosed bounded ordinary metadata reaches the local process.
- Raw inputs, vectors, and response payloads are not logged or persisted.
- Reduced cache uses one-way input identities and complete versions.
- Failure and absence are neutral and cannot block Candidate publication.
- Model output is observational and cannot create an executable group in this slice.

## Implementation steps

1. Add Domain input/model/vector/comparison values and finite/symmetric tests.
2. Add Application ports/options/orchestration with fake cold/warm/failure/progress
   tests.
3. Add fixed-loopback Ollama adapter and reduced pair cache with fake HTTP/filesystem
   tests.
4. Integrate observational phase/run metrics and WPF disclosure without changing
   decisions.
5. Add opt-in real-model corpus evaluation command/test.
6. Run it only when a versioned local model is available; otherwise record the
   environment blocker without inventing calibration results.
7. Reconcile docs, archive plan when implementation and available verification are
   complete, and create the threshold-calibration follow-up only after real outputs.

## Tests

- canonical input hash changes for title/author/language/preprocessing changes;
- vector dimension/finite bounds and symmetric cosine permille;
- no raw metadata/vector in cache files or assertion/log messages;
- target only still-unresolved pair members; deterministic dedup/batching/caps;
- warm comparison run makes zero model calls; corruption/version/model changes miss;
- unavailable/missing model, timeout, malformed/count/dimension/nonfinite/oversize
  responses fall back without changing pair decisions;
- fixed `127.0.0.1:11434` endpoint, `/api/embed`, model/input/truncate fields, no
  Authorization header, no redirects/retries;
- matching baseline remains semantically unchanged in ordinary tests;
- opt-in output contains only opaque IDs, versions, integer similarity, and aggregates;
- architecture tests prove no runtime types in Domain and no mutation dependency.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

Optional real-model evaluation after configuration:

```powershell
$env:CALIBRE_OLLAMA_EMBEDDING_MODEL = 'embeddinggemma'
$env:CALIBRE_OLLAMA_EMBEDDING_MODEL_VERSION = '<local-model-digest>'
$env:CALIBRE_RUN_LOCAL_MODEL_EVALUATION = '1'
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj `
  --filter FullyQualifiedName~LocalModelCorpusEvaluation `
  --logger "console;verbosity=detailed"
```

## Risks

- Metadata embeddings may mostly repeat deterministic title/author evidence. Real
  calibration must prove incremental value before fusion.
- Model names are mutable tags. Cache/calibration validity requires an explicit local
  digest/version, not only a friendly name.
- Different hardware/runtime versions may produce small numeric drift. Store integer
  permille observations and runtime/model identity; define tolerance only from data.
- A running local model can consume substantial memory/CPU. Strict batches, timeout,
  one active request, progress, and no auto-start/pull bound resource use.
- Ten repeated synthetic positives are insufficient to choose a trustworthy
  threshold. Add diverse public/synthetic hard negatives before activation.

## Unresolved questions

- Select and install a specific embedding model and record its immutable local digest.
  The current environment has no Ollama executable, so real calibration is blocked.
- Decide whether metadata-only embeddings provide enough incremental value or whether
  the next calibrated model must consume privacy-safe derived EPUB structure/content.

## Progress

- [x] Current residual cases and model architecture inspected.
- [x] Ollama API and local environment checked.
- [x] Observational-first safety boundary selected.
- [x] Domain local embedding values implemented.
- [x] Application orchestration and reduced cache ports implemented.
- [x] Ollama adapter/cache and fake-based tests implemented.
- [x] Observational UI/run metrics implemented.
- [x] Opt-in real-model calibration executed with a versioned model.
- [x] Documentation reconciled and standard verification passed with 690/690 tests.

## Final outcome

Implemented an observational fixed-loopback Ollama embedding path with bounded
metadata inputs, memory-only finite vectors, symmetric integer cosine comparison,
complete input/runtime/model/preprocessing/policy identities, reduced atomic cache,
batching/input limits, fail-open behavior, progress/run metrics, and UI disclosure.
Observations do not alter Candidate evidence, decisions, groups, or the reviewed
matching baseline.

Installed and evaluated:

- Ollama `0.32.13`;
- `embeddinggemma:latest`, local model ID `85462619ee72`;
- immutable model blob
  `sha256-0800cbac9c2064dde519420e75e512a83cb360de3ad5df176185dc69652fc515`;
- 256 dimensions, preprocessing `local-metadata-embedding-input/1.0.0`, comparison
  `local-embedding-cosine/1.0.0`.

Measured cosine permille:

- positives: 864, 857, 850, 862;
- hard negatives: 696, 789, 801, 753.

The observed gap is promising but only eight cases were measured, with the highest
hard negative at 801 and lowest positive at 850. No production threshold or model
Candidate evidence was accepted. The next plan must expand independently reviewed
positive/hard-negative coverage before considering a threshold between those bands.
