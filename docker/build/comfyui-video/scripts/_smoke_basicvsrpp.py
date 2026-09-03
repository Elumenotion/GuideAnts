"""One-shot BasicVSR++ load + upscale smoke test."""
from __future__ import annotations

import numpy as np

from basicvsrpp_fg import default_checkpoint_dir, load_basicvsrpp, upscale_fg_sequence


def main() -> None:
    checkpoint_dir = default_checkpoint_dir()
    print(f"loading from {checkpoint_dir}", flush=True)
    module = load_basicvsrpp(checkpoint_dir, "cuda:0")
    param = next(module.parameters())
    print(f"loaded dtype={param.dtype} device={param.device}", flush=True)
    frames = [np.random.rand(64, 64, 3).astype(np.float32) for _ in range(4)]
    out = upscale_fg_sequence(frames, module, "cuda:0", length=4)
    print(
        f"out n={len(out)} shape={out[0].shape} dtype={out[0].dtype} "
        f"min={float(out[0].min()):.4f} max={float(out[0].max()):.4f}",
        flush=True,
    )


if __name__ == "__main__":
    main()
