# Catalog and Library

AI Nexus uses a local SQLite catalog for source metadata and a separate, private view of acquisition receipts from explicitly tracked model folders. This is the first catalog increment, not the completed Steam/Playnite/Ninite feature set.

## Current behavior

- Confirming workloads searches the selected categories. Successful online results are saved with their original source retrieval times.
- Model identity is repository + pinned revision + exact artifact path. Category names are relationships, not part of artifact identity. One file found under Coding and Assistants becomes one catalog entry with both categories.
- Tool identity is its source repository. Runtime release identity includes the repository and release tag. Tools and runtime metadata do not establish model compatibility.
- The Library reads saved facts and local receipt metadata. Text filtering and the receipt filter remain offline. The explicit “Search source in Discover” action performs a fresh source search and never selects a result or approves a download.
- Original model-file indexing, explicit integrity checks and recovery reviews remain available under “Model folders & file checks.”

## Evidence remains separate

Listed does not mean installed. A local receipt records an earlier acquisition, not current file integrity. File existence and byte length do not establish matching content. A resource estimate does not establish offline execution or performance. The Library does not automatically run integrity or inference tests.

Private receipt-derived entries can appear before their source has been searched. Their source dates remain historical and they are not written into the public-metadata catalog. Paths, receipts, prompts, replies, machine scans and local test results are not included in public catalog rows. No community upload or export is implemented.

Image/video repository listings still lack dedicated complete-model and runtime requirement recipes. They remain unassessed. Coding/agent category membership is search scope, not proof of coding quality or tool-use ability.

## Storage and recovery

The default is the current user's local application-data directory under `AINexus/Catalog/catalog.db`. A portable package may include `catalog-root.txt` containing one absolute data-directory path. The deployed development package uses the owner's stable data folder on F:. An explicit catalog folder selected in the UI takes precedence and is saved as a storage preference; it never restores workload selections or approvals.

“Open catalog folder” opens a different local catalog location. It does not move, merge or delete the old database or model files. A missing database is created only when source entries are first saved. An unreadable database, unknown schema, invalid source record or conflicting pinned hash is reported rather than reset. SQLite transactions protect complete imports. Cancellation rolls back unfinished imports.

The first schema supports 10,000 entries and bounded payloads. Reaching the limit rejects the import without evicting old entries. Receipt indexing supports 64 explicit folders, bounded depth/directory/receipt counts, and skips junctions and invalid/external destinations with warnings. Existing receipt files are the durable private record; no parallel private database is required yet.

## Next increments

Complete model/runtime dependency recipes for media workloads; evidence-backed hardware requirements; explicit installation queue and progress; verified local launch links; updates/rollback; versioned metadata import/export; and reviewed community submissions. Each requires its own verification gates. No hosted service, account or paid subscription is required for this local catalog.

## Dependency sources

The implementation uses [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/). Portable packages retain the actual dependency versions, package copyright/license declarations and upstream license/notice files. Vendored notice source URLs and hashes are recorded in `docs/third-party/sqlite/sources.json`; the publisher rejects unreviewed dependency-version changes.
