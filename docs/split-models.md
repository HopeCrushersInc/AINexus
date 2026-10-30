# Split text models

A large GGUF model can consist of several files. Nexus treats a complete supported inventory as one selectable model. It does not offer a lone part as a whole model.

1. Search a chosen text task or inspect an exact repository.
2. Select a model variant and a drive. Fit estimates use the sum of all part sizes, plus disclosed memory/storage allowances.
3. Review every pinned source, filename, byte count, SHA-256 and destination. Approve that exact inventory and its terms.
4. Download or verify matching existing files. A different existing file is preserved. Cancellation removes only the owned partial transfer; earlier verified parts remain.
5. Explicitly select a compatible existing engine, verify and launch. Nexus locks and hashes every part during load and the initial response test. It passes the first part's local path to the selected engine; nothing is fetched through an engine repository shortcut.

The supported naming convention is `name-00001-of-00002.gguf`, followed by every consecutive part in the same folder, with the same base name and count. Inventories contain 2–512 files, all from one pinned revision, and every file must have positive source-reported bytes and a SHA-256. A source tree is still limited to its first 1,000 entries; incomplete sets remain unavailable.

Receipts store a separate hash for each file. There is no invented aggregate file hash. The library checks every part's presence and size before offering a load review; actual hashing happens again before launch. Old single-file receipts remain valid. A locally derived test model does not establish publisher authenticity, model quality, or support for every architecture.

This follows the first-part loading convention of the optional [llama.cpp loader](https://github.com/ggml-org/llama.cpp/pull/6187). Runtime support and actual model loading still require verification. Nexus bundles no engine or weights.

Combined RAM/VRAM planning, arbitrary tensor placement and a complete split-set repair workflow remain unfinished. To reacquire damaged sets, prepare a fresh full-inventory review in a separate folder; existing files are preserved. Splitting files does not reduce the model's total memory requirement.
