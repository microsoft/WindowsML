# Copyright (C) Microsoft Corporation. All rights reserved.

"""Calibrate the int8 key and value cache scales for Qwen3.8-27B-NVFP4.

Windows ML can hold the attention key and value (KV) cache of a model as 8-bit
integers. Each cache channel is stored as an integer times a static scale that
this script computes. It runs calibration prompts through the model, records
the largest absolute value each K and V channel reaches in the cache, and
writes the scales to kv_cache_scales.safetensors beside the model.

  scale = max(amax, 1e-4) / 127 * margin

K is taken after its norm and rotary embedding, as the cache stores it. RoPE
rotates the two halves of each rotary pair together, so either half can reach
the pair's norm; both get sqrt(a_i^2 + a_j^2). The default margins, 1.0 for K
and 1.5 for V, leave V room for prompts that exceed the calibration maxima.

The script needs only PyTorch, Hugging Face Transformers, safetensors, and
tokenizers, and runs on the CPU or a CUDA GPU. It runs the text model one
decoder layer at a time, and each layer over blocks of tokens, so memory
depends little on prompt length: about 2 bytes per prompt token per hidden
unit (10 KB per token) for the hidden states, plus a few GB for one layer's
weights and one block's activations. It prints its peak memory at the end.

Usage:
  python calibrate_kv_scales.py <model folder> --prompts prompts.jsonl
  python calibrate_kv_scales.py <model folder> --prompts prompts.jsonl --output scales.safetensors

prompts.jsonl has one JSON object per line with a "prompt" string, already
rendered with the chat template; build_calibration_prompts.py writes one from
public sources. Use long prompts, and cover the content the model will serve:
source code, prose, conversations, tool calls, reasoning, and other languages
each reach channels the others don't.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import sys
import time
from pathlib import Path

import torch
from safetensors import safe_open
from safetensors.torch import save_file
from tokenizers import Tokenizer
from transformers import AutoConfig, DynamicCache
from transformers.models.qwen3_5 import modeling_qwen3_5 as qwen

# Values of the FP4 (E2M1) codes, indexed by the 4-bit code.
FP4_VALUES = torch.tensor((0.0, 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0, -0.0, -0.5, -1.0, -1.5, -2.0, -3.0, -4.0, -6.0))
NVFP4_BLOCK = 16
DEQUANTIZE_ROWS = 2048


class Checkpoint:
    """Reads tensors from a safetensors checkpoint and dequantizes them on a device."""

    def __init__(self, folder: Path, device: torch.device):
        index = json.loads((folder / "model.safetensors.index.json").read_text(encoding="utf-8"))
        self.weight_map: dict[str, str] = index["weight_map"]
        self.folder = folder
        self.device = device

    def has(self, name: str) -> bool:
        return name in self.weight_map

    def raw(self, name: str) -> torch.Tensor:
        # Each read opens the file and closes it, so the mapped checkpoint
        # pages don't accumulate in the process as the layers go by.
        with safe_open(self.folder / self.weight_map[name], framework="pt", device="cpu") as file:
            return file.get_tensor(name).to(self.device)

    def weight(self, name: str, dtype: torch.dtype) -> torch.Tensor:
        """Returns a weight, dequantized from NVFP4 or FP8 when the checkpoint quantizes it."""
        base = name.removesuffix(".weight")
        tensor = self.raw(name)
        if self.has(base + ".weight_scale_2"):
            # NVFP4: two 4-bit codes per byte, low nibble first; an FP8 scale per
            # block of 16 values and an FP32 scale per tensor. Dequantized a few
            # rows at a time to bound the temporary memory.
            scales = self.raw(base + ".weight_scale")
            global_scale = self.raw(base + ".weight_scale_2").float()
            values = FP4_VALUES.to(self.device)
            out = torch.empty(tensor.shape[0], tensor.shape[1] * 2, dtype=dtype, device=self.device)
            for row in range(0, tensor.shape[0], DEQUANTIZE_ROWS):
                packed = tensor[row: row + DEQUANTIZE_ROWS]
                codes = torch.stack((packed & 0x0F, packed >> 4), dim=-1).reshape(packed.shape[0], -1).int()
                block = scales[row: row + DEQUANTIZE_ROWS].float().repeat_interleave(NVFP4_BLOCK, dim=-1)
                out[row: row + DEQUANTIZE_ROWS] = (values[codes] * block * global_scale).to(dtype)
            return out
        if tensor.dtype == torch.float8_e4m3fn:
            # FP8: one FP32 scale per tensor.
            return (tensor.float() * self.raw(base + ".weight_scale").float()).to(dtype)
        return tensor.to(dtype)


def load_layer(layer: torch.nn.Module, checkpoint: Checkpoint, prefix: str, dtype: torch.dtype) -> None:
    state = {}
    for name, parameter in layer.state_dict().items():
        source = prefix + name
        if not checkpoint.has(source):
            raise KeyError(f"{source} is not in the checkpoint")
        value = checkpoint.weight(source, dtype) if name.endswith(".weight") else checkpoint.raw(source).to(dtype)
        if value.shape != parameter.shape:
            raise ValueError(f"{source} has shape {tuple(value.shape)}; the layer expects {tuple(parameter.shape)}")
        # The gated delta net's decay and time-step parameters stay in FP32.
        state[name] = value.float() if name in ("linear_attn.A_log", "linear_attn.dt_bias") else value
    layer.load_state_dict(state, strict=True)


def amax(states: torch.Tensor) -> torch.Tensor:
    """Largest magnitude per [kv_head, channel] of [batch, kv_heads, positions, head_dim] states."""
    return states.detach().float().abs().amax(dim=(0, 2)).cpu()


def attention_layer(layer, x, rotary, config, block, query_block, k_max, v_max):
    """Runs a full-attention decoder layer over x in blocks; folds the cache's |K| and |V| maxima.

    Each block appends its K and V to a cache on the device, then attends its
    queries, a few rows at a time, over the cache so far.
    """
    attn = layer.self_attn
    device = next(layer.parameters()).device
    length = x.shape[1]
    kv_heads, head_dim = config.num_key_value_heads, config.head_dim
    groups = config.num_attention_heads // kv_heads
    keys = torch.empty(kv_heads, length, head_dim, dtype=torch.float32, device=device)
    values = torch.empty_like(keys)
    # One score buffer, reused through views of the size each step needs, so the
    # allocator doesn't keep a block for every attention span.
    scores_buffer = torch.empty(groups * min(query_block, length) * length, dtype=torch.float32, device=device)
    upper = torch.ones(query_block, query_block, dtype=torch.bool, device=device).triu_(1)
    out = torch.empty_like(x)
    for start in range(0, length, block):
        residual = x[:, start: start + block].to(device)
        end = start + residual.shape[1]
        h = layer.input_layernorm(residual)
        shape = (*h.shape[:-1], -1, head_dim)
        q, gate = torch.chunk(attn.q_proj(h).view(*h.shape[:-1], -1, head_dim * 2), 2, dim=-1)
        q = attn.q_norm(q.view(shape)).transpose(1, 2)
        k = attn.k_norm(attn.k_proj(h).view(shape)).transpose(1, 2)
        v = attn.v_proj(h).view(shape).transpose(1, 2)
        positions = torch.arange(start, end, device=device).unsqueeze(0)
        cos, sin = rotary(h, positions)
        q, k = qwen.apply_rotary_pos_emb(q, k, cos, sin)
        k_max.append(amax(k))
        v_max.append(amax(v))
        keys[:, start:end] = k[0].float()
        values[:, start:end] = v[0].float()

        context = torch.empty_like(q)
        # Causal attention for one KV head's query group and a few query rows at
        # a time, with FP32 scores, so the memory it needs stays bounded.
        for head in range(kv_heads):
            heads = slice(head * groups, (head + 1) * groups)
            for q_start in range(start, end, query_block):
                q_end = min(q_start + query_block, end)
                rows = slice(q_start - start, q_end - start)
                scores = scores_buffer[: groups * (q_end - q_start) * q_end].view(groups, q_end - q_start, q_end)
                torch.matmul(q[0, heads, rows].float(), keys[head, :q_end].T, out=scores)
                scores.mul_(attn.scaling)
                # Only the block's own columns reach future positions.
                scores[:, :, q_start:q_end].masked_fill_(upper[: q_end - q_start, : q_end - q_start], float("-inf"))
                scores.sub_(scores.amax(dim=-1, keepdim=True)).exp_()
                scores.div_(scores.sum(dim=-1, keepdim=True))
                context[0, heads, rows] = (scores @ values[head, :q_end]).to(q.dtype)
        hidden = context.transpose(1, 2).reshape(*h.shape[:-1], -1) * torch.sigmoid(gate.reshape(*h.shape[:-1], -1))
        hidden = residual + attn.o_proj(hidden)
        hidden = hidden + layer.mlp(layer.post_attention_layernorm(hidden))
        out[:, start:end] = hidden.cpu()
    return out


def linear_attention_layer(layer, x, config, block):
    """Runs a linear-attention decoder layer over x in blocks; the cache carries the state between blocks."""
    device = next(layer.parameters()).device
    cache = DynamicCache(config=config)
    out = torch.empty_like(x)
    for start in range(0, x.shape[1], block):
        residual = x[:, start: start + block].to(device)
        hidden = layer.linear_attn(hidden_states=layer.input_layernorm(residual), cache_params=cache)
        hidden = residual + hidden
        hidden = hidden + layer.mlp(layer.post_attention_layernorm(hidden))
        out[:, start: start + block] = hidden.cpu()
    return out


def peak_memory_gb() -> float:
    if sys.platform == "win32":
        class Counters(ctypes.Structure):
            _fields_ = [("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong)] + [
                (name, ctypes.c_size_t) for name in ("PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
                                                     "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage",
                                                     "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage")]
        counters = Counters()
        counters.cb = ctypes.sizeof(counters)
        kernel32 = ctypes.WinDLL("kernel32")
        kernel32.GetCurrentProcess.restype = ctypes.c_void_p
        kernel32.K32GetProcessMemoryInfo.argtypes = (ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_ulong)
        kernel32.K32GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb)
        return counters.PeakWorkingSetSize / 2**30
    import resource
    return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 2**20


def model_revision(model: Path) -> str:
    # hf download records the commit of each file it downloads.
    for metadata in sorted((model / ".cache" / "huggingface" / "download").glob("*.metadata")):
        return metadata.read_text(encoding="utf-8").splitlines()[0].strip()
    return ""


def write_scales(path: Path, config, k_max: dict, v_max: dict, margins: tuple[float, float],
                 metadata: dict[str, str]) -> None:
    rotary = int(config.head_dim * config.rope_parameters.get("partial_rotary_factor", 1.0))
    half = rotary // 2
    tensors = {}
    for layer in sorted(k_max):
        k = k_max[layer].clone()
        # Split-half RoPE pairs channel j with channel j + rotary / 2.
        pair = torch.sqrt(k[:, :half] ** 2 + k[:, half:rotary] ** 2)
        k[:, :half] = pair
        k[:, half:rotary] = pair
        for kind, maximum, margin in (("k", k, margins[0]), ("v", v_max[layer], margins[1])):
            scale = torch.clamp(maximum, min=1e-4) / 127.0 * margin
            tensors[f"model.layers.{layer}.self_attn.{kind}_cache_scale"] = scale.unsqueeze(1).float().contiguous()
    save_file(tensors, str(path), metadata=metadata)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("model", type=Path, help="the nvidia/Qwen3.8-27B-NVFP4 folder")
    parser.add_argument("--prompts", type=Path, required=True, help='JSON Lines of {"prompt": ...}')
    parser.add_argument("--output", type=Path, help="defaults to kv_cache_scales.safetensors in the model folder")
    parser.add_argument("--margin", default="1,1.5", help="K and V scale margins (default 1,1.5)")
    parser.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu")
    parser.add_argument("--dtype", choices=("bfloat16", "float16", "float32"), default="bfloat16")
    parser.add_argument("--block-tokens", type=int, default=4096, help="tokens per block (a multiple of 64)")
    parser.add_argument("--query-block", type=int, default=512, help="query rows per attention step")
    args = parser.parse_args()
    if args.block_tokens % 64:
        parser.error("--block-tokens must be a multiple of 64")

    margin_values = [float(m) for m in args.margin.split(",")]
    margins = (margin_values[0], margin_values[-1])
    dtype = getattr(torch, args.dtype)
    device = torch.device(args.device)
    output = args.output or args.model / "kv_cache_scales.safetensors"

    config = AutoConfig.from_pretrained(args.model)
    text = getattr(config, "text_config", config)
    tokenizer = Tokenizer.from_file(str(args.model / "tokenizer.json"))
    checkpoint = Checkpoint(args.model, device)
    prefix = "model.language_model."

    prompts = [json.loads(line)["prompt"] for line in args.prompts.read_text(encoding="utf-8").splitlines()
               if line.strip()]
    token_ids = [tokenizer.encode(p, add_special_tokens=False).ids for p in prompts]
    total = sum(len(ids) for ids in token_ids)
    print(f"{len(prompts)} prompts, {total:,} tokens, longest {max(map(len, token_ids)):,}; "
          f"{device}, {args.dtype}, blocks of {args.block_tokens}", flush=True)

    rotary = qwen.Qwen3_5TextRotaryEmbedding(text).to(device)
    embed = checkpoint.weight(prefix + "embed_tokens.weight", dtype)
    hidden = [embed[torch.tensor(ids, device=device)].unsqueeze(0).cpu() for ids in token_ids]
    del embed

    k_max: dict[int, torch.Tensor] = {}
    v_max: dict[int, torch.Tensor] = {}
    started = time.perf_counter()
    with torch.inference_mode():
        for index in range(text.num_hidden_layers):
            # Built on the meta device: the checkpoint supplies every weight.
            with torch.device("meta"):
                layer = qwen.Qwen3_5DecoderLayer(text, index).to(dtype)
            layer.to_empty(device=device)
            load_layer(layer, checkpoint, f"{prefix}layers.{index}.", dtype)
            layer.eval()
            full = text.layer_types[index] == "full_attention"
            ks, vs = [], []
            for p, states in enumerate(hidden):
                if full:
                    hidden[p] = attention_layer(layer, states, rotary, text, args.block_tokens, args.query_block, ks, vs)
                else:
                    hidden[p] = linear_attention_layer(layer, states, text, args.block_tokens)
            if full:
                k_max[index] = torch.stack(ks).amax(dim=0)
                v_max[index] = torch.stack(vs).amax(dim=0)
            del layer
            gpu = ""
            if device.type == "cuda":
                torch.cuda.empty_cache()
                gpu = (f", GPU {torch.cuda.memory_allocated() / 2**30:.1f} GB in use, "
                       f"{torch.cuda.max_memory_allocated() / 2**30:.1f} GB peak, "
                       f"{torch.cuda.max_memory_reserved() / 2**30:.1f} GB reserved")
            print(f"layer {index + 1}/{text.num_hidden_layers} ({text.layer_types[index]}): "
                  f"{(time.perf_counter() - started) / 60:.1f} min, peak {peak_memory_gb():.1f} GB{gpu}", flush=True)

    metadata = {
        "format": "pt",
        "rule": "amax/127 over the prompts' KV cache rows; K rotary pairs share their norm bound",
        "margin": f"{margins[0]:.6f},{margins[1]:.6f}",
        "prompts": str(len(prompts)),
        "tokens": str(total),
        "prompts_sha256": hashlib.sha256(args.prompts.read_bytes()).hexdigest(),
        "base_model": "nvidia/Qwen3.8-27B-NVFP4",
        "base_model_revision": model_revision(args.model),
        "tool": "calibrate_kv_scales.py",
    }
    write_scales(output, text, k_max, v_max, margins, metadata)
    print(f"Wrote {len(k_max) * 2} scale tensors to {output}; peak memory {peak_memory_gb():.1f} GB")
    if device.type == "cuda":
        print(f"Peak GPU memory {torch.cuda.max_memory_allocated() / 2**30:.1f} GB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
