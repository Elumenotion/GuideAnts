# Corpus re-embed runbook (D3)

Execute once at cutover when switching the default embeddings model from Harrier to
`Qwen3-Embedding-0.6B` (or any other catalog entry).

## Preconditions

- Phase 1 embeddings facade is live with the target GGUF loaded (`/emb/ready` green).
- Retrieval parity harness has been run on a representative corpus (record results in
  [`acceptance-evidence.md`](./acceptance-evidence.md)).

## Steps

1. Put the API in maintenance mode or pause background embedding jobs.
2. Confirm `LocalEmbeddingService.SourceVectorDimensions` matches the active model's
   `producedDimension` (1024 for Qwen3 default).
3. Trigger a full rebuild of document chunk embeddings via the existing `RebuildEmbeddings`
   background path (or operator script used in your environment).
4. Verify sample queries return sensible top-k after re-embed completes.
5. Record job id / duration / document count in `acceptance-evidence.md`.

Mixed Harrier + Qwen3 vectors in the same index are not supported — the cutover must be
all-or-nothing per deployment.
