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

## Installed-model review and loading

For supported GGUF text models, choose a Library model and then a specific local copy in the installation selector. Neither choice is made automatically, including when only one copy exists. **Review local model** reads that private acquisition receipt and shows its exact path, pinned artifact, expected hash, recorded license and source date. It does not search the internet, download files, scan hardware or launch an engine.

The saved source remains historical. Review requires its artifact, destination and plan fingerprint to match the chosen installation; it also checks that the model still exists with the recorded size. Missing, conflicting, malformed or changed evidence prevents preparation. Review does not approve a fresh acquisition, and download actions are disabled for this installed-copy flow.

The separate **Load model and open chat** action re-reads and binds the receipt to the reviewed plan, hashes the current model, scans current hardware and requires the installed-model capacity check to pass. A read lease blocks writes or replacement throughout verification, hardware checking, engine startup and initial generation. An existing engine must be selected, and an already running portal-owned model must be stopped explicitly before another can load. Chat opens only after actual local generation succeeds. Cancellation and failure preserve files and clean up any server started by that load.

This flow does not implement automatic server switching, media-model launch adapters or model repair. The engine's offline-mode flag and loopback communication are not an operating-system network-isolation guarantee.

Six live Library checks passed for each of two existing text models, including initial generation, two chat turns and owned-server cleanup. These runs reused local weights and engines without refreshing online metadata or downloading replacements. They validate those particular model/runtime combinations, not the entire catalog or multi-GPU execution.

## Storage and recovery

The default is the current user's local application-data directory under `AINexus/Catalog/catalog.db`. A portable deployment may use `catalog-root.txt` containing one absolute data-directory path to keep a separate stable local catalog. An explicit catalog folder selected in the UI takes precedence and is saved as a storage preference; it never restores workload selections or approvals.

“Open catalog folder” opens a different local catalog location. It does not move, merge or delete the old database or model files. A missing database is created only when source entries are first saved. An unreadable database, unknown schema, invalid source record or conflicting pinned hash is reported rather than reset. SQLite transactions protect complete imports. Cancellation rolls back unfinished imports.

The first schema supports 10,000 entries and bounded payloads. Reaching the limit rejects the import without evicting old entries. Receipt indexing supports 64 explicit folders, bounded depth/directory/receipt counts, and skips junctions and invalid/external destinations with warnings. Existing receipt files are the durable private record; no parallel private database is required yet.

## Next increments

Complete model/runtime dependency recipes for media workloads; evidence-backed hardware requirements; explicit installation queue and progress; broader model/tool launch adapters; updates/rollback; versioned metadata import/export; and reviewed community submissions. Each requires its own verification gates. No hosted service, account or paid subscription is required for this local catalog.

## Dependency sources

The implementation uses [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/). Portable packages retain the actual dependency versions, package copyright/license declarations and upstream license/notice files. Vendored notice source URLs and hashes are recorded in `docs/third-party/sqlite/sources.json`; the publisher rejects unreviewed dependency-version changes.

## Image artifact source facts

Image searches now import pinned Safetensors candidates from public Hugging Face text-to-image repositories alongside distinct GitHub tool entries. This uses the existing artifact identity/schema. Metadata is not proof of a complete checkpoint, companion assets, runtime compatibility or hardware fit. The image candidates remain NeedsDetails, are shown with an unverified notice, and require explicit selection for source review. Selecting and reviewing an image candidate opens its separate data-download setup. A fresh exact review and approval are required before acquisition; no engine setup is performed. Missing byte counts/hashes/licenses remain unknown, and conflicting duplicate source facts are rejected.

Image downloads save private receipts under the selected folder's .ainexus-image-artifact-receipts directory. These are separate from GGUF receipts and grant no text-model launch approval. The receipt records actual byte/hash verification and reuse while keeping checkpoint completeness, hardware fit and runtime execution false. Neither weights nor private receipts are included in releases or source commits.

## Private image-folder configuration

Connecting a checkpoint folder to an already installed portable ComfyUI engine is a separate review/apply operation. It is not granted by source discovery, an image download or a receipt. The user chooses the engine and folder, reviews exact configuration contents, existing hash and recovery-backup path, then approves that specific write. Changed inputs or stale results invalidate the review; approvals are not remembered.

The dedicated `ainexus-approved-checkpoints.json` holds at most 16 explicitly approved existing local folder paths. Applying preserves original `ainexus-models.yaml` bytes and keeps a uniquely named backup when replacing the dedicated config. Only the next explicit engine start reads the installed mapping alongside existing YAML; applying never selects a model or starts/stops an engine. Folder paths persist as private installed configuration, separate from session-only workload/model choices.

This mapping and its backups contain private local paths and are excluded from source/release assets and the public source-metadata catalog. A connected folder is not a complete-checkpoint, current-integrity, hardware-fit or successful-generation certificate. Unknown or malformed dedicated configuration is reported rather than overwritten. A post-publication conflict reports possible changes and both configuration/backup paths for manual review; it does not attempt a risky automatic rollback.
