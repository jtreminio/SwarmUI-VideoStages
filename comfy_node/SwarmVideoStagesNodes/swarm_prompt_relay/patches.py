"""LTX cross-attention patching for PromptRelay.

Ported from WhatDreamsCost-ComfyUI/patches.py
"""

import logging
import types

import comfy.ldm.modules.attention

log = logging.getLogger(__name__)


def _make_masked_override(prev_override):
    """Route mask-bearing attention through attention_pytorch (sage/etc. drop arbitrary
    masks); chain to prior override when unmasked so other backends aren't clobbered."""
    def override(func, *args, **kwargs):
        if kwargs.get("mask") is not None:
            return comfy.ldm.modules.attention.attention_pytorch(*args, **kwargs)
        if prev_override is not None:
            return prev_override(func, *args, **kwargs)
        return func(*args, **kwargs)
    return override


def _make_ltx_mask_wrapper(underlying, mask_fn, attr):
    """Wrap an LTX cross-attn forward (default or another node's patch), adding
    PromptRelay's mask via the `mask` kwarg the upstream signature already accepts.
    `underlying` must already be bound to its module.
    """
    def wrapped(_self, x, context=None, mask=None, pe=None, k_pe=None, transformer_options={}):
        if context is not None:
            opts = {**transformer_options, "promptrelay_attn_type": attr}
            pr_mask = mask_fn(x.shape[1], context.shape[1], x.dtype, x.device, opts)
            if pr_mask is not None:
                mask = pr_mask if mask is None else mask + pr_mask

        if mask is not None:
            prev = transformer_options.get("optimized_attention_override")
            transformer_options = {
                **transformer_options,
                "optimized_attention_override": _make_masked_override(prev),
            }

        return underlying(
            x, context=context, mask=mask, pe=pe, k_pe=k_pe,
            transformer_options=transformer_options,
        )

    return wrapped


def detect_ltx(model):
    """Validate the model is LTX; return its VAE temporal stride (the pixel->latent
    temporal compression, for converting pixel frame counts to latent frames).
    """
    diff_model = model.model.diffusion_model

    if hasattr(diff_model, "patchifier"):
        return int(diff_model.vae_scale_factors[0])

    raise ValueError(
        f"Unsupported model type: {type(diff_model).__name__}. "
        f"PromptRelay in SwarmUI-VideoStages currently supports LTX models only."
    )


def apply_patches(model_clone, mask_fn):
    diffusion_model = model_clone.get_model_object("diffusion_model")

    for idx, block in enumerate(diffusion_model.transformer_blocks):
        for attr in ("attn2", "audio_attn2"):
            module = getattr(block, attr, None)
            if module is None:
                continue
            key = f"diffusion_model.transformer_blocks.{idx}.{attr}.forward"
            # get_model_object returns the prior patch if present, else the default bound forward.
            underlying = model_clone.get_model_object(key)
            wrapper = _make_ltx_mask_wrapper(underlying, mask_fn, attr)
            model_clone.add_object_patch(key, types.MethodType(wrapper, module))
