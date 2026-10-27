# Featured model sources and verification

Catalog entries are source preferences, not compatibility guarantees. Exact public metadata is refreshed and pinned before download approval. Sizes and hashes come from the source, never from model-name guesses.

| Entry | Primary source | Declared license at review | Local evidence |
| --- | --- | --- | --- |
| Qwen 2.5 32B | [Community GGUF](https://huggingface.co/bartowski/Qwen2.5-32B-Instruct-GGUF) | Apache-2.0 | Actual 19,851,336,576-byte Q4_K_M acquisition, hash verification, owned offline-mode load and two chat turns |
| Qwen 2.5 Coder 7B | [Official GGUF](https://huggingface.co/Qwen/Qwen2.5-Coder-7B-Instruct-GGUF) | Apache-2.0 | Actual 4,683,073,536-byte Q4_K_M reuse/hash verification, owned offline-mode load and two chat turns |
| Qwen 2.5 7B | [Community GGUF](https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF) | Apache-2.0 | Metadata and fixture transport checks; this catalog variant has no live claim |
| Stheno 3.2 8B (NSFW) | [GGUF](https://huggingface.co/bartowski/L3-8B-Stheno-v3.2-GGUF), [upstream](https://huggingface.co/Sao10K/L3-8B-Stheno-v3.2) | CC-BY-NC-4.0; review base-model terms too | Source describes mixed SFW/NSFW storywriting. Not locally generated/tested by this increment |
| Lexi Llama 3 8B (NSFW) | [GGUF](https://huggingface.co/bartowski/Lexi-Llama-3-8B-Uncensored-GGUF), [upstream](https://huggingface.co/Orenguteng/Lexi-Llama-3-8B-Uncensored) | Llama 3 terms | Publisher describes it as uncensored. No promise of particular behavior; not locally generated/tested by this increment |

Adult/less-filtered entries are hidden until a fresh explicit NSFW opt-in. That setting is never persisted. Labels describe source claims, not results established by the portal. A model's size, license and content orientation are separate from whether it runs on a particular PC.

Actual development-PC live checks used an RTX 3090 with 24 GiB VRAM, 64 GiB RAM, an existing verified llama.cpp b11669 engine and an enforced 250 W limit. They establish short generation and history forwarding, not broad coding quality, image capability, full-context fit, UPS headroom or OS-enforced network isolation.

The existing image studio supports explicitly selected installed ComfyUI checkpoints. Only the earlier SD1.5 workflow has established live image evidence; a second image model and automatic text/image lifecycle are unfinished work saved outside this release.
