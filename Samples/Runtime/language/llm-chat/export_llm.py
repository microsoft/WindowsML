# Copyright (C) Microsoft Corporation. All rights reserved.

"""Prepare fixed-shape language-model artifacts for the Runtime samples.

What this preparation script writes
  - emb.onnx, decoder.onnx, and head.onnx for the split Runtime pipeline.
  - model.onnx for the unified Runtime language path.
  - tokenizer files and runtime_pipeline.json metadata consumed by the samples.

What the generated artifacts teach
  - Fixed token, position, mask, logits, and KV-cache tensor shapes so samples can
    declare state pairs before pipeline Build.
  - Split stages that make Runtime Connect, per-stage placement, and logits
    readback visible in language/llm-chat.
  - A unified decoder artifact that hello-language-model and unified chat load
    with the same Runtime LoadModelFromFile call used for GGUF artifacts.

Run it
  python export_llm.py -m <source-model-id-or-path> -o <output-directory>
  python export_llm.py -m <source-model-id-or-path> -o <output-directory> --verify

Learn more (paths relative to this file)
  ../../../../docs/Runtime/tutorials/03-language-models.md
  ../../../../docs/api-reference/CommonPatterns.md
  ../../../../docs/Runtime/artifacts.md
"""

import argparse
import atexit
import hashlib
import importlib.metadata
import json
import math
import os
from pathlib import Path
import shutil
import tempfile
import uuid

import onnx


GENERATED_FILES = {
    "emb.onnx",
    "decoder.onnx",
    "head.onnx",
    "prefill.onnx",
    "model.onnx",
    "task_model.onnx",
    "task_decode.onnx",
    "tokenizer.json",
    "tokenizer_config.json",
    "special_tokens_map.json",
    "added_tokens.json",
    "vocab.json",
    "merges.txt",
    "tokenizer.model",
    "spiece.model",
    "sentencepiece.bpe.model",
    "chat_template.jinja",
    "runtime_pipeline.json",
    "winml_export_info.json",
}


# Tokenizer metadata normalization used by all exported language paths.
def normalize_token_ids(*candidates):
    """Return unique non-negative token IDs from scalar or sequence metadata."""

    result = []
    for candidate in candidates:
        if candidate is None:
            continue
        values = candidate if isinstance(candidate, (list, tuple, set)) else (candidate,)
        for value in values:
            if isinstance(value, bool):
                continue
            try:
                token_id = int(value)
            except (TypeError, ValueError):
                continue
            if token_id >= 0 and token_id not in result:
                result.append(token_id)
    return result


def pad_prefill_input_ids(token_ids, max_sequence_length, pad_token_id=0):
    """Right-pad a non-empty prompt to the prefill artifact's static capacity."""

    if (
        isinstance(max_sequence_length, bool)
        or not isinstance(max_sequence_length, int)
        or max_sequence_length <= 0
    ):
        raise ValueError("max_sequence_length must be a positive integer")
    prompt = list(token_ids)
    if not prompt:
        raise ValueError("prefill requires at least one prompt token")
    if len(prompt) > max_sequence_length:
        raise ValueError("prompt exceeds the prefill sequence capacity")
    if isinstance(pad_token_id, bool) or not isinstance(pad_token_id, int) or pad_token_id < 0:
        raise ValueError("pad_token_id must be a non-negative integer")
    return prompt + [pad_token_id] * (max_sequence_length - len(prompt))


# Runtime pipeline metadata records stage connections and state ordinals for the
# split sample without changing the generated ONNX graphs.
def runtime_pipeline_manifest(
    eos_token_ids,
    max_sequence_length=128,
    attention_mask_data_type="float16",
    num_hidden_layers=1,
):
    """Describe decode plus the optional fixed-shape prefill stage by ordinal."""

    if (
        isinstance(max_sequence_length, bool)
        or not isinstance(max_sequence_length, int)
        or max_sequence_length <= 0
    ):
        raise ValueError("max_sequence_length must be a positive integer")
    if attention_mask_data_type not in {"float16", "float32"}:
        raise ValueError("attention_mask_data_type must be float16 or float32")
    if (
        isinstance(num_hidden_layers, bool)
        or not isinstance(num_hidden_layers, int)
        or num_hidden_layers <= 0
    ):
        raise ValueError("num_hidden_layers must be a positive integer")

    autoregressive = {
        "token_input": {"stage": "embedding", "index": 0},
        "output": {"stage": "head", "index": 0, "kind": "logits"},
        "state_owner": "decoder",
        "feedback_mode": "session_driven",
        "token_data_type": "int64",
        "sequence_capacity": max_sequence_length,
        "step_inputs": [
            {
                "stage": "decoder",
                "index": 1,
                "kind": "sequence_position",
                "data_type": "int64",
                "shape": [1],
            },
            {
                "stage": "decoder",
                "index": 2,
                "kind": "causal_attention_mask",
                "data_type": attention_mask_data_type,
                "shape": [1, 1, 1, max_sequence_length],
            },
        ],
    }
    end_tokens = normalize_token_ids(eos_token_ids)
    if end_tokens:
        autoregressive["end_tokens"] = end_tokens

    return {
        "stages": [
            {"id": "embedding", "file": "emb.onnx", "target": "model"},
            {"id": "decoder", "file": "decoder.onnx", "target": "model"},
            {"id": "head", "file": "head.onnx", "target": "model"},
        ],
        "connections": [
            {
                "source": "embedding",
                "output": 0,
                "target": "decoder",
                "input": 0,
            },
            {
                "source": "decoder",
                "output": 0,
                "target": "head",
                "input": 0,
            },
        ],
        "tokenizer": "tokenizer.json",
        "autoregressive": autoregressive,
        "prefill": {
            "stage": {
                "id": "prefill",
                "file": "prefill.onnx",
                "target": "model",
            },
            "token_input": {
                "index": 0,
                "data_type": "int64",
                "shape": [1, max_sequence_length],
                "padding": "right",
            },
            "attention_mask_input": {
                "index": 1,
                "data_type": attention_mask_data_type,
                "shape": [1, 1, max_sequence_length, max_sequence_length],
                "kind": "causal_attention_mask",
            },
            "final_token_selector_input": {
                "index": 2,
                "data_type": attention_mask_data_type,
                "shape": [1, max_sequence_length, 1],
                "kind": "one_hot_final_token",
            },
            "logits_output": {
                "index": 0,
                "kind": "logits",
                "sequence_axis": 1,
                "valid_position": 0,
            },
            "kv_cache_mappings": [
                {
                    "prefill_output": 1 + kv_index,
                    "decode_stage": "decoder",
                    "decode_input": 3 + kv_index,
                    "decode_output": 1 + kv_index,
                }
                for kv_index in range(num_hidden_layers * 2)
            ],
        },
    }


def write_runtime_pipeline_manifest(
    output_directory,
    eos_token_ids,
    max_sequence_length=128,
    attention_mask_data_type="float16",
    num_hidden_layers=1,
):
    with open(
        os.path.join(output_directory, "runtime_pipeline.json"),
        "w",
        encoding="utf-8",
    ) as manifest_file:
        json.dump(
            runtime_pipeline_manifest(
                eos_token_ids,
                max_sequence_length,
                attention_mask_data_type,
                num_hidden_layers,
            ),
            manifest_file,
            indent=2,
        )
        manifest_file.write("\n")


class StagedOutputDirectory:
    def __init__(self, destination):
        self.destination = os.path.abspath(destination)
        if os.path.exists(self.destination):
            if not os.path.isdir(self.destination):
                raise ValueError(f"Output path is not a directory: {self.destination}")
            unexpected = [
                entry.name
                for entry in os.scandir(self.destination)
                if entry.name not in GENERATED_FILES
            ]
            if unexpected:
                raise ValueError(
                    "Refusing to overwrite a directory with unrelated content: "
                    + ", ".join(sorted(unexpected))
                )

        parent = os.path.dirname(self.destination)
        os.makedirs(parent, exist_ok=True)
        self.path = tempfile.mkdtemp(
            prefix=f".{os.path.basename(self.destination)}.staging-",
            dir=parent,
        )
        atexit.register(self.cleanup)

    def cleanup(self):
        if self.path and os.path.isdir(self.path):
            shutil.rmtree(self.path)

    def publish(self):
        backup = None
        if os.path.exists(self.destination):
            backup = f"{self.destination}.backup-{uuid.uuid4().hex}"
            os.replace(self.destination, backup)

        try:
            os.replace(self.path, self.destination)
            self.path = None
        except Exception:
            if backup and not os.path.exists(self.destination):
                os.replace(backup, self.destination)
            raise

        if backup:
            shutil.rmtree(backup)


def main():
    p = argparse.ArgumentParser(description="Export a decoder LLM as fixed-dimension ONNX models.")
    p.add_argument("--model", "-m", required=True, help="HF model id or local path")
    p.add_argument("--revision", help="Pinned Hugging Face model revision")
    p.add_argument("--driver-sha256", help="Hash of the invoking export driver")
    p.add_argument("--output", "-o", required=True, help="Output directory")
    p.add_argument("--max-seq", type=int, default=128, help="Fixed KV sequence length / context (default 128)")
    p.add_argument("--dtype", choices=["fp16", "fp32"], default="fp16")
    p.add_argument("--verify", action="store_true", help="Numerically verify the decoder against HF after export")
    args = p.parse_args()

    import torch
    os.environ["TORCH_ONNX_USE_NEW_EXPORTER"] = "0"
    from transformers import AutoModelForCausalLM, AutoTokenizer

    staged_output = StagedOutputDirectory(args.output)
    out_dir = staged_output.path

    MAX_SEQ = args.max_seq
    tdt = torch.float16 if args.dtype == "fp16" else torch.float32

    print(f"Loading {args.model} ...")
    pretrained_args = {"revision": args.revision} if args.revision else {}
    model = AutoModelForCausalLM.from_pretrained(
        args.model,
        dtype=tdt,
        **pretrained_args,
    ).eval()
    cfg = model.config
    resolved_revision = getattr(cfg, "_commit_hash", None)
    if args.revision and resolved_revision != args.revision:
        raise ValueError(
            f"resolved model revision {resolved_revision!r} does not match "
            f"requested revision {args.revision!r}"
        )
    tcfg = getattr(cfg, "text_config", cfg)

    def gc(name, default=None):
        return getattr(tcfg, name, getattr(cfg, name, default))

    HIDDEN = gc("hidden_size")
    NH = gc("num_attention_heads")
    NKV = gc("num_key_value_heads", NH)
    HD = gc("head_dim") or (HIDDEN // NH)
    NL = gc("num_hidden_layers")
    VOCAB = gc("vocab_size")
    model_type = (gc("model_type") or "").lower()
    family = "gemma" if model_type.startswith("gemma") else ("qwen" if model_type.startswith("qwen") else model_type)

    # Attention scale: Gemma uses query_pre_attn_scalar**-0.5; others use head_dim**-0.5.
    qpas = gc("query_pre_attn_scalar")
    attn_scale = (qpas ** -0.5) if qpas else (HD ** -0.5)

    # Embedding scale: Gemma multiplies token embeddings by sqrt(hidden). Modern
    # transformers folds this into a Gemma3TextScaledWordEmbedding module whose
    # forward already applies the scale, so calling the real module would
    # double-scale. Only apply a manual scale when the embedding does NOT self-scale.
    emb_self_scaled = hasattr(model.model.embed_tokens, "embed_scale")
    emb_scale = 1.0 if (emb_self_scaled or family != "gemma") else math.sqrt(HIDDEN)

    # Soft-capping (Gemma2). None on Gemma3+.
    attn_cap = gc("attn_logit_softcapping")
    final_cap = gc("final_logit_softcapping")

    # Per-layer RoPE base: Gemma3 uses a local base on sliding layers and a global
    # base on full-attention layers. transformers 5.x stores these in a
    # rope_parameters dict keyed by attention type (full_attention / sliding_attention),
    # not as flat rope_theta / rope_local_base_freq scalars.
    rope_params = gc("rope_parameters") or gc("rope_scaling") or {}

    def theta_for(key):
        if isinstance(rope_params, dict):
            sub = rope_params.get(key)
            if isinstance(sub, dict) and sub.get("rope_theta"):
                return sub["rope_theta"]
            if rope_params.get("rope_theta"):
                return rope_params["rope_theta"]
        return None

    rope_theta = gc("rope_theta") or theta_for("full_attention") or 10000.0
    rope_local = gc("rope_local_base_freq") or theta_for("sliding_attention")
    layer_types = gc("layer_types")
    # transformers 5.x consolidates RoPE config; the authoritative frequencies are
    # the model's own rotary-embedding inv_freq buffers. Prefer those.
    rotary = getattr(model.model, "rotary_emb", None)
    rotary_local = getattr(model.model, "rotary_emb_local", None)

    print(f"  family={family} type={model_type}  L={NL} H={NH} KV={NKV} HD={HD} hidden={HIDDEN} vocab={VOCAB}")
    print(f"  attn_scale={attn_scale:.5f} emb_scale={emb_scale:.3f} "
          f"attn_cap={attn_cap} final_cap={final_cap} rope_theta={rope_theta} rope_local={rope_local}")
    print(f"  tied_emb={getattr(cfg,'tie_word_embeddings',None)}")

    layers = model.model.layers
    # Detect a representative layer's structure once.
    l0 = layers[0]
    fused_qkv = hasattr(l0.self_attn, "qkv_proj")
    fused_gate_up = hasattr(l0.mlp, "gate_up_proj")
    has_qk_norm = hasattr(l0.self_attn, "q_norm") and l0.self_attn.q_norm is not None
    # Gemma2/3 wrap both sublayers in a norm sandwich (post_attention_layernorm is
    # applied to the attention OUTPUT, and a pre/post feedforward norm pair wraps the
    # MLP). Standard decoders (Llama/Qwen/Phi) use only input + post_attention norms.
    gemma_norms = hasattr(l0, "pre_feedforward_layernorm")
    print(f"  fused_qkv={fused_qkv} fused_gate_up={fused_gate_up} qk_norm={has_qk_norm} gemma_norms={gemma_norms}")

    def build_rope_from_inv(inv_freq):
        inv = inv_freq.detach().to(torch.float32).cpu()
        ang = torch.outer(torch.arange(MAX_SEQ, dtype=torch.float32), inv)
        return torch.cos(ang).to(tdt), torch.sin(ang).to(tdt)

    def build_rope_from_theta(base):
        inv = 1.0 / (base ** (torch.arange(0, HD, 2, dtype=torch.float32) / HD))
        return build_rope_from_inv(inv)

    # Prefer the model's own rotary frequencies (inherits exact theta/scaling);
    # fall back to a theta computation only if no rotary module is exposed.
    if rotary is not None and getattr(rotary, "inv_freq", None) is not None:
        cos_g, sin_g = build_rope_from_inv(rotary.inv_freq)
        print(f"  RoPE(global): from model.rotary_emb.inv_freq ({rotary.inv_freq.shape[0]} freqs)")
    else:
        cos_g, sin_g = build_rope_from_theta(rope_theta)
        print(f"  RoPE(global): computed from theta={rope_theta}")

    if rotary_local is not None and getattr(rotary_local, "inv_freq", None) is not None:
        cos_l, sin_l = build_rope_from_inv(rotary_local.inv_freq)
        print("  RoPE(local):  from model.rotary_emb_local.inv_freq")
    elif rope_local:
        cos_l, sin_l = build_rope_from_theta(rope_local)
        print(f"  RoPE(local):  computed from rope_local={rope_local}")
    else:
        cos_l, sin_l = cos_g, sin_g

    def is_local(i):
        if layer_types and i < len(layer_types):
            return "sliding" in str(layer_types[i]).lower()
        return False

    # ---- EMB (with optional Gemma embedding scale) ------------------------
    class Emb(torch.nn.Module):
        def __init__(self, embed, scale):
            super().__init__()
            self.embed = embed
            self.scale = scale

        def forward(self, input_ids):
            h = self.embed(input_ids)
            if self.scale != 1.0:
                h = h * torch.tensor(self.scale, dtype=h.dtype)
            return h

    # ---- HEAD (final norm + lm_head, with optional final soft-cap) --------
    class Head(torch.nn.Module):
        def __init__(self, norm, lm_head, cap):
            super().__init__()
            self.norm = norm
            self.lm_head = lm_head
            self.cap = cap

        def forward(self, hidden_state):
            logits = self.lm_head(self.norm(hidden_state))
            if self.cap:
                logits = torch.tanh(logits / self.cap) * self.cap
            return logits

    def apply_rope(x, pos_id, cos_t, sin_t):
        # cos/sin table width == number of rotary frequencies. For full rotary it
        # equals HD/2; for partial rotary (Phi-3/Phi-4-mini, partial_rotary_factor<1)
        # it is smaller, so only the first 2*half dims are rotated and the tail of
        # each head passes through unchanged (standard partial-RoPE).
        half = cos_t.shape[-1]
        positions = pos_id.reshape(-1).to(torch.long)
        cos = cos_t.index_select(0, positions).view(1, 1, -1, half)
        sin = sin_t.index_select(0, positions).view(1, 1, -1, half)
        rot = x[..., : 2 * half]
        x1, x2 = rot[..., :half], rot[..., half:]
        rotated = torch.cat([x1 * cos - x2 * sin, x2 * cos + x1 * sin], dim=-1)
        if 2 * half == x.shape[-1]:
            return rotated
        return torch.cat([rotated, x[..., 2 * half:]], dim=-1)

    class FixedDecoder(torch.nn.Module):
        def __init__(self, fixed_step=False, prefill=False):
            super().__init__()
            self.layers = layers
            self.fixed_step = fixed_step
            self.prefill = prefill
            if fixed_step and prefill:
                raise ValueError("fixed_step and prefill are mutually exclusive")
            self.register_buffer("cos_g", cos_g)
            self.register_buffer("sin_g", sin_g)
            self.register_buffer("cos_l", cos_l)
            self.register_buffer("sin_l", sin_l)
            if fixed_step:
                self.register_buffer(
                    "cache_positions",
                    torch.arange(MAX_SEQ, dtype=torch.long).view(1, 1, MAX_SEQ, 1),
                )

        def forward(self, hidden_state, position_id, attn_mask, *flat_kv):
            h = hidden_state
            seq_len = 1 if self.fixed_step else hidden_state.shape[1]
            present = []
            for i, layer in enumerate(self.layers):
                if not self.prefill:
                    past_k = flat_kv[i * 2]
                    past_v = flat_kv[i * 2 + 1]
                attn = layer.self_attn

                residual = h
                hn = layer.input_layernorm(h)

                if fused_qkv:
                    qkv = attn.qkv_proj(hn)
                    qs, ks = NH * HD, NKV * HD
                    q = qkv[:, :, :qs]
                    k = qkv[:, :, qs:qs + ks]
                    v = qkv[:, :, qs + ks:]
                else:
                    q = attn.q_proj(hn)
                    k = attn.k_proj(hn)
                    v = attn.v_proj(hn)

                q = q.view(1, seq_len, NH, HD).transpose(1, 2)
                k = k.view(1, seq_len, NKV, HD).transpose(1, 2)
                v = v.view(1, seq_len, NKV, HD).transpose(1, 2)

                # QK-norm before RoPE (Gemma3 / Qwen3), inherited from the modules.
                if has_qk_norm:
                    q = attn.q_norm(q)
                    k = attn.k_norm(k)

                cos_t = self.cos_l if is_local(i) else self.cos_g
                sin_t = self.sin_l if is_local(i) else self.sin_g
                q = apply_rope(q, position_id, cos_t, sin_t)
                k = apply_rope(k, position_id, cos_t, sin_t)

                # Write new K/V into the fixed buffer at position_id (standard op).
                if self.prefill:
                    present_k = k
                    present_v = v
                elif self.fixed_step:
                    update_mask = self.cache_positions == position_id.view(1, 1, 1, 1)
                    present_k = torch.where(update_mask, k, past_k)
                    present_v = torch.where(update_mask, v, past_v)
                else:
                    pos_idx = position_id.reshape(1, 1, seq_len, 1).expand(1, NKV, seq_len, HD)
                    present_k = past_k.scatter(2, pos_idx, k)
                    present_v = past_v.scatter(2, pos_idx, v)

                # Expand GQA KV groups to full head count via repeat_interleave.
                if NKV != NH:
                    rep = NH // NKV
                    k_full = present_k.repeat_interleave(rep, dim=1)
                    v_full = present_v.repeat_interleave(rep, dim=1)
                else:
                    k_full, v_full = present_k, present_v

                scores = torch.matmul(q, k_full.transpose(-2, -1)) * attn_scale
                if attn_cap:
                    scores = torch.tanh(scores / attn_cap) * attn_cap
                scores = scores + attn_mask
                probs = torch.softmax(scores, dim=-1)
                ctx = torch.matmul(probs, v_full)
                ctx = ctx.transpose(1, 2).contiguous().view(1, seq_len, NH * HD)
                attn_out = attn.o_proj(ctx)
                # Gemma2/3 normalize the attention output before the residual add.
                if gemma_norms:
                    attn_out = layer.post_attention_layernorm(attn_out)
                h = residual + attn_out

                # MLP via the real submodules (handles SwiGLU/GeGLU + activation).
                residual = h
                if gemma_norms:
                    hn = layer.pre_feedforward_layernorm(h)
                else:
                    hn = layer.post_attention_layernorm(h)
                if fused_gate_up:
                    gu = layer.mlp.gate_up_proj(hn)
                    gate, up = gu.chunk(2, dim=-1)
                    mlp_out = layer.mlp.down_proj(layer.mlp.act_fn(gate) * up) \
                        if hasattr(layer.mlp, "act_fn") else \
                        layer.mlp.down_proj(layer.mlp.activation_fn(gate) * up)
                else:
                    act = getattr(layer.mlp, "act_fn", None) or getattr(layer.mlp, "activation_fn")
                    mlp_out = layer.mlp.down_proj(act(layer.mlp.gate_proj(hn)) * layer.mlp.up_proj(hn))

                # Gemma2/3 normalize the MLP output before the residual add.
                if gemma_norms:
                    mlp_out = layer.post_feedforward_layernorm(mlp_out)
                h = residual + mlp_out

                present.append(present_k)
                present.append(present_v)
            return (h, *present)

    class Prefill(torch.nn.Module):
        def __init__(self, embed, decoder, head):
            super().__init__()
            self.embed = embed
            self.decoder = decoder
            self.head = head
            self.register_buffer(
                "positions",
                torch.arange(MAX_SEQ, dtype=torch.long),
            )

        def forward(self, input_ids, attention_mask, final_token_selector):
            hidden = self.embed(input_ids)
            decoded = self.decoder(hidden, self.positions, attention_mask)
            final_hidden = torch.sum(
                decoded[0] * final_token_selector,
                dim=1,
                keepdim=True,
            )
            logits = self.head(final_hidden)
            return (logits, *decoded[1:])

    class UnifiedDecoder(torch.nn.Module):
        def __init__(self, embed, decoder, head):
            super().__init__()
            self.embed = embed
            self.decoder = decoder
            self.head = head
            self.register_buffer(
                "key_positions",
                torch.arange(MAX_SEQ, dtype=torch.long).view(1, 1, 1, MAX_SEQ),
            )

        def forward(self, input_ids, past_seq_len, *flat_kv):
            input_ids = input_ids.reshape(1, -1)
            seq_len = input_ids.shape[1]
            start = past_seq_len.reshape(-1)[0].to(torch.long)
            positions = torch.arange(seq_len, dtype=torch.long, device=input_ids.device) + start
            query_positions = positions.view(1, 1, seq_len, 1)
            allowed = self.key_positions <= query_positions
            zero = torch.zeros((), dtype=tdt, device=input_ids.device)
            blocked = torch.full((), torch.finfo(tdt).min, dtype=tdt, device=input_ids.device)
            attention_mask = torch.where(allowed, zero, blocked)

            hidden = self.embed(input_ids)
            decoded = self.decoder(hidden, positions, attention_mask, *flat_kv)
            logits = self.head(decoded[0])
            return (logits, *decoded[1:])

    class TaskDecoder(torch.nn.Module):
        def __init__(self, unified):
            super().__init__()
            self.unified = unified

        def forward(self, input_ids, task_state):
            flat_kv = []
            state_offset = 0
            for _ in range(NL):
                flat_kv.append(
                    task_state[:, state_offset : state_offset + NKV, :, :]
                )
                state_offset += NKV
                flat_kv.append(
                    task_state[:, state_offset : state_offset + NKV, :, :]
                )
                state_offset += NKV

            position_bytes = (
                task_state[:, state_offset : state_offset + 1, 0, :4]
                .reshape(1, 4)
                .to(torch.int32)
            )
            past_seq_len = (
                position_bytes[:, 0:1]
                + position_bytes[:, 1:2] * 256
                + position_bytes[:, 2:3] * 65536
                + position_bytes[:, 3:4] * 16777216
            )
            decoded = self.unified(input_ids, past_seq_len, *flat_kv)
            next_position = (
                past_seq_len +
                input_ids.reshape(-1).shape[0]
            )
            # Compute the byte split with floating-point ONNX operators. The
            # sequence position is non-negative, so this matches integer
            # division and remainder for the exported range.
            next_position_float = next_position.to(torch.float32)
            position_byte_values = torch.cat(
                [
                    (
                        lambda scaled: (
                            scaled - torch.floor(scaled / 256.0) * 256.0
                        ).to(tdt)
                    )(
                        torch.floor(
                            next_position_float / float(256 ** byte_index)
                        )
                    )
                    for byte_index in range(4)
                ],
                dim=1,
            ).reshape(1, 1, 1, 4)
            position_first_row = torch.cat(
                [
                    position_byte_values,
                    torch.zeros(
                        1,
                        1,
                        1,
                        HD - 4,
                        dtype=tdt,
                        device=input_ids.device,
                    ),
                ],
                dim=3,
            )
            position_state = torch.cat(
                [
                    position_first_row,
                    torch.zeros(
                        1,
                        1,
                        MAX_SEQ - 1,
                        HD,
                        dtype=tdt,
                        device=input_ids.device,
                    ),
                ],
                dim=2,
            )
            packed_state = torch.cat(
                [*decoded[1:], position_state],
                dim=1,
            )
            return decoded[0].float(), packed_state

    def export(mod, sample, in_names, out_names, path, dynamic_axes=None):
        with torch.no_grad():
            torch.onnx.export(mod, sample, path, input_names=in_names, output_names=out_names,
                              opset_version=14, do_constant_folding=True, dynamo=False,
                              dynamic_axes=dynamic_axes)

    print("\nExporting emb.onnx ...")
    export(Emb(model.model.embed_tokens, emb_scale), (torch.zeros(1, 1, dtype=torch.long),),
           ["input_ids"], ["hidden_state"], os.path.join(out_dir, "emb.onnx"))

    print("Exporting head.onnx ...")
    export(Head(model.model.norm, model.lm_head, final_cap), (torch.randn(1, 1, HIDDEN, dtype=tdt),),
           ["hidden_state"], ["logits"], os.path.join(out_dir, "head.onnx"))

    print("Exporting decoder.onnx (a few minutes) ...")
    dec = FixedDecoder(fixed_step=True).to(tdt).eval()
    hidden = torch.randn(1, 1, HIDDEN, dtype=tdt)
    pos = torch.tensor([0], dtype=torch.long)
    mask = torch.zeros(1, 1, 1, MAX_SEQ, dtype=tdt)
    mask[:, :, :, 1:] = float("-inf")
    flat = []
    for _ in range(NL):
        flat.append(torch.zeros(1, NKV, MAX_SEQ, HD, dtype=tdt))
        flat.append(torch.zeros(1, NKV, MAX_SEQ, HD, dtype=tdt))
    in_names = ["hidden_state", "position_id", "attention_mask"]
    out_names = ["hidden_out"]
    for i in range(NL):
        in_names += [f"past_key_values.{i}.key", f"past_key_values.{i}.value"]
        out_names += [f"present.{i}.key", f"present.{i}.value"]
    export(dec, (hidden, pos, mask, *flat), in_names, out_names, os.path.join(out_dir, "decoder.onnx"))

    print("Exporting prefill.onnx (fixed-shape batched prompt) ...")
    prefill = Prefill(
        Emb(model.model.embed_tokens, emb_scale),
        FixedDecoder(prefill=True).to(tdt).eval(),
        Head(model.model.norm, model.lm_head, final_cap),
    ).to(tdt).eval()
    prefill_input_names = [
        "input_ids",
        "attention_mask",
        "final_token_selector",
    ]
    prefill_output_names = ["logits"]
    for i in range(NL):
        prefill_output_names += [f"present.{i}.key", f"present.{i}.value"]
    export(
        prefill,
        (
            torch.zeros(1, MAX_SEQ, dtype=torch.long),
            torch.zeros(1, 1, MAX_SEQ, MAX_SEQ, dtype=tdt),
            torch.nn.functional.one_hot(
                torch.tensor([min(4, MAX_SEQ) - 1]),
                num_classes=MAX_SEQ,
            ).to(tdt).reshape(1, MAX_SEQ, 1),
        ),
        prefill_input_names,
        prefill_output_names,
        os.path.join(out_dir, "prefill.onnx"),
    )

    print("Exporting model.onnx (unified Runtime decoder) ...")
    unified_input_names = ["input_ids", "past_seq_len"]
    unified_output_names = ["logits"]
    for i in range(NL):
        unified_input_names += [f"past_key_values.{i}.key", f"past_key_values.{i}.value"]
        unified_output_names += [f"present.{i}.key", f"present.{i}.value"]
    unified = UnifiedDecoder(
        Emb(model.model.embed_tokens, emb_scale),
        FixedDecoder().to(tdt).eval(),
        Head(model.model.norm, model.lm_head, final_cap),
    ).to(tdt).eval()
    prompt_ids = torch.zeros(4, dtype=torch.long)
    past_seq_len = torch.zeros(1, 1, dtype=torch.int32)
    export(
        unified,
        (prompt_ids, past_seq_len, *flat),
        unified_input_names,
        unified_output_names,
        os.path.join(out_dir, "model.onnx"),
        dynamic_axes={
            "input_ids": {0: "sequence"},
            "logits": {1: "sequence"},
        },
    )

    print("Exporting task_model.onnx (Task-native packed state) ...")
    if HD < 4:
        raise RuntimeError(
            "Task packed-state export requires a head dimension of at least 4"
        )
    task_state_channels = NL * NKV * 2 + 1
    task_state = torch.zeros(
        1,
        task_state_channels,
        MAX_SEQ,
        HD,
        dtype=tdt,
    )
    task_decoder = TaskDecoder(unified).to(tdt).eval()
    export(
        task_decoder,
        (torch.zeros(1, 4, dtype=torch.long), task_state),
        ["input_ids", "task_state"],
        ["logits", "task_state_out"],
        os.path.join(out_dir, "task_model.onnx"),
        dynamic_axes={
            "input_ids": {1: "sequence"},
            "logits": {1: "sequence"},
        },
    )
    task_model_path = os.path.join(out_dir, "task_model.onnx")
    task_onnx = onnx.load(task_model_path)
    task_logits_shape = (
        task_onnx.graph.output[0].type.tensor_type.shape.dim
    )
    task_logits_shape[0].ClearField("dim_param")
    task_logits_shape[0].dim_value = 1
    onnx.checker.check_model(task_onnx)
    onnx.save(task_onnx, task_model_path)

    print("Exporting task_decode.onnx (fixed one-token packed state) ...")
    # Same packed-state format as task_model.onnx, specialized for one token.
    task_decode_unified = UnifiedDecoder(
        Emb(model.model.embed_tokens, emb_scale),
        FixedDecoder(fixed_step=True).to(tdt).eval(),
        Head(model.model.norm, model.lm_head, final_cap),
    ).to(tdt).eval()
    task_decoder_fixed = TaskDecoder(task_decode_unified).to(tdt).eval()
    export(
        task_decoder_fixed,
        (torch.zeros(1, 1, dtype=torch.long), task_state),
        ["input_ids", "task_state"],
        ["logits", "task_state_out"],
        os.path.join(out_dir, "task_decode.onnx"),
    )

    print("Saving tokenizer and Runtime pipeline manifest ...")
    tokenizer = AutoTokenizer.from_pretrained(args.model, **pretrained_args)
    tokenizer.save_pretrained(out_dir)
    write_runtime_pipeline_manifest(
        out_dir,
        normalize_token_ids(
            getattr(tokenizer, "eos_token_id", None),
            getattr(tcfg, "eos_token_id", None),
            getattr(cfg, "eos_token_id", None),
        ),
        MAX_SEQ,
        "float16" if args.dtype == "fp16" else "float32",
        NL,
    )

    # Sidecar capabilities note (for sample family selection; NOT a
    # genai_config and not required by the Runtime).
    info = {
        "exporter_sha256": hashlib.sha256(
            Path(__file__).read_bytes()
        ).hexdigest(),
        "export_driver_sha256": args.driver_sha256,
        "model_id": args.model,
        "source_revision": resolved_revision,
        "dtype": args.dtype,
        "dependencies": {
            package: importlib.metadata.version(package)
            for package in ("torch", "transformers", "onnx")
        },
        "family": family, "model_type": model_type, "hidden": HIDDEN, "layers": NL,
        "heads": NH, "kv_heads": NKV, "head_dim": HD, "vocab": VOCAB, "max_seq": MAX_SEQ,
        "tied_embeddings": bool(getattr(cfg, "tie_word_embeddings", False)),
        "fused_qkv": fused_qkv, "fused_gate_up": fused_gate_up, "qk_norm": has_qk_norm,
        "emb_scale": emb_scale, "attn_scale": attn_scale,
        "attn_softcap": attn_cap, "final_softcap": final_cap,
        "rope_theta": rope_theta, "rope_local": rope_local,
        "eos_token_id": getattr(cfg, "eos_token_id", None),
    }
    with open(os.path.join(out_dir, "winml_export_info.json"), "w") as f:
        json.dump(info, f, indent=2, default=str)

    if args.verify:
        verify(
            args.model,
            args.revision,
            out_dir,
            model,
            tcfg,
            tdt,
            MAX_SEQ,
        )

    total = sum(os.path.getsize(os.path.join(out_dir, f)) for f in os.listdir(out_dir))
    staged_output.publish()
    final_dir = staged_output.destination
    print(f"\n{'='*60}\nExport complete: {final_dir}")
    print(f"  family={family}  total={total/1e9:.2f} GB  max_seq={MAX_SEQ}  dtype={args.dtype}")
    print(f"  Hello: .\\run_hello_language_model.ps1 -ModelPath \"{final_dir}\\model.onnx\"")
    print(f"  Chat:  .\\run_llm_chat.ps1 -ModelPath \"{final_dir}\\model.onnx\" -Device cpu")


def verify(model_id, revision, out_dir, hf_model, tcfg, tdt, max_seq):
    # Compare the fixed-dim ONNX pipeline's first-token argmax against HF on a
    # short prompt as a quick numerical check before running the C++ sample.
    import numpy as np
    import torch
    try:
        import onnxruntime as ort
    except ImportError as exc:
        raise RuntimeError(
            "verify requires onnxruntime; install the exporter dependencies and retry"
        ) from exc
    from transformers import AutoTokenizer

    tok = AutoTokenizer.from_pretrained(
        model_id,
        **({"revision": revision} if revision else {}),
    )
    text = "The sky is often"
    ids = tok(text, return_tensors="pt").input_ids[0].tolist()

    NL = getattr(tcfg, "num_hidden_layers")
    NKV = getattr(tcfg, "num_key_value_heads", getattr(tcfg, "num_attention_heads"))
    HD = getattr(tcfg, "head_dim", None) or (getattr(tcfg, "hidden_size") // getattr(tcfg, "num_attention_heads"))

    so = ort.SessionOptions()
    emb = ort.InferenceSession(os.path.join(out_dir, "emb.onnx"), so)
    dec = ort.InferenceSession(os.path.join(out_dir, "decoder.onnx"), so)
    head = ort.InferenceSession(os.path.join(out_dir, "head.onnx"), so)
    prefill = ort.InferenceSession(os.path.join(out_dir, "prefill.onnx"), so)
    unified = ort.InferenceSession(os.path.join(out_dir, "model.onnx"), so)
    task_model = ort.InferenceSession(
        os.path.join(out_dir, "task_model.onnx"),
        so,
    )
    task_decode = ort.InferenceSession(
        os.path.join(out_dir, "task_decode.onnx"),
        so,
    )
    np_dt = np.float16 if tdt == torch.float16 else np.float32

    def decode_task_position(state):
        return sum(
            int(state[0, -1, 0, byte_index]) << (8 * byte_index)
            for byte_index in range(4)
        )

    kv = {f"past_key_values.{i}.key": np.zeros((1, NKV, max_seq, HD), np_dt) for i in range(NL)}
    kv.update({f"past_key_values.{i}.value": np.zeros((1, NKV, max_seq, HD), np_dt) for i in range(NL)})

    last_logits = None
    for pos, tid in enumerate(ids):
        hid = emb.run(None, {"input_ids": np.array([[tid]], np.int64)})[0]
        mask = np.full((1, 1, 1, max_seq), np.finfo(np_dt).min, np_dt)
        mask[:, :, :, : pos + 1] = 0
        d_in = {"hidden_state": hid.astype(np_dt), "position_id": np.array([pos], np.int64),
                "attention_mask": mask}
        d_in.update(kv)
        d_out = dec.run(None, d_in)
        names = [o.name for o in dec.get_outputs()]
        hid_out = d_out[names.index("hidden_out")]
        for i in range(NL):
            kv[f"past_key_values.{i}.key"] = d_out[names.index(f"present.{i}.key")]
            kv[f"past_key_values.{i}.value"] = d_out[names.index(f"present.{i}.value")]
        last_logits = head.run(None, {"hidden_state": hid_out.astype(np_dt)})[0]

    onnx_tok = int(np.argmax(last_logits[0, -1]))

    unified_inputs = {
        "input_ids": np.array(ids, np.int64),
        "past_seq_len": np.array([[0]], np.int32),
    }
    unified_inputs.update({
        f"past_key_values.{i}.key": np.zeros((1, NKV, max_seq, HD), np_dt)
        for i in range(NL)
    })
    unified_inputs.update({
        f"past_key_values.{i}.value": np.zeros((1, NKV, max_seq, HD), np_dt)
        for i in range(NL)
    })
    unified_outputs = unified.run(None, unified_inputs)
    unified_names = [output.name for output in unified.get_outputs()]
    unified_logits = unified_outputs[unified_names.index("logits")]
    unified_tok = int(np.argmax(unified_logits[0, -1]))
    unified_kv = {}
    for i in range(NL):
        unified_kv[f"past_key_values.{i}.key"] = unified_outputs[
            unified_names.index(f"present.{i}.key")
        ]
        unified_kv[f"past_key_values.{i}.value"] = unified_outputs[
            unified_names.index(f"present.{i}.value")
        ]

    task_logits_shape = task_model.get_outputs()[0].shape
    if task_logits_shape[0] != 1:
        raise RuntimeError(
            "verify failed: Task logits must declare a static batch dimension "
            f"of 1, got {task_logits_shape}"
        )
    task_state_channels = NL * NKV * 2 + 1
    task_state = np.zeros(
        (1, task_state_channels, max_seq, HD),
        np_dt,
    )
    task_outputs = task_model.run(
        None,
        {
            "input_ids": np.array([ids], np.int64),
            "task_state": task_state,
        },
    )
    task_logits = task_outputs[0]
    task_state = task_outputs[1]
    task_tok = int(np.argmax(task_logits[0, -1]))
    task_position = decode_task_position(task_state)
    task_position_match = task_position == len(ids)
    packed_unified_kv = np.concatenate(
        [
            unified_kv[f"past_key_values.{i}.{kind}"]
            for i in range(NL)
            for kind in ("key", "value")
        ],
        axis=1,
    )
    task_kv_max_diff = float(
        np.max(
            np.abs(
                task_state[:, :-1, :, :].astype(np.float32)
                - packed_unified_kv.astype(np.float32)
            )
        )
    )
    task_kv_match = task_kv_max_diff <= (
        5e-1 if np_dt == np.float16 else 1e-4
    )
    task_dynamic_prompt_match = True
    task_dynamic_prompt_results = []
    for dynamic_text in ("Hello", "Water freezes"):
        dynamic_ids = tok(
            dynamic_text,
            return_tensors="pt",
        ).input_ids
        dynamic_state = np.zeros(
            (1, task_state_channels, max_seq, HD),
            np_dt,
        )
        dynamic_outputs = task_model.run(
            None,
            {
                "input_ids": dynamic_ids.numpy().astype(np.int64),
                "task_state": dynamic_state,
            },
        )
        dynamic_task_token = int(
            np.argmax(dynamic_outputs[0][0, -1])
        )
        dynamic_position = decode_task_position(dynamic_outputs[1])
        with torch.no_grad():
            dynamic_hf_token = int(
                torch.argmax(hf_model(dynamic_ids).logits[0, -1])
            )
        dynamic_match = (
            dynamic_task_token == dynamic_hf_token
            and dynamic_position == dynamic_ids.shape[1]
        )
        task_dynamic_prompt_match = (
            task_dynamic_prompt_match and dynamic_match
        )
        task_dynamic_prompt_results.append(
            (
                dynamic_text,
                dynamic_ids.shape[1],
                dynamic_position,
                dynamic_task_token,
                dynamic_hf_token,
                dynamic_match,
            )
        )

    pad_token_ids = normalize_token_ids(
        getattr(tok, "pad_token_id", None),
        getattr(tok, "eos_token_id", None),
        getattr(tcfg, "eos_token_id", None),
    )
    padded_ids = pad_prefill_input_ids(
        ids,
        max_seq,
        pad_token_ids[0] if pad_token_ids else 0,
    )
    prefill_mask = np.full(
        (1, 1, max_seq, max_seq),
        np.finfo(np_dt).min,
        np_dt,
    )
    for query_position in range(len(ids)):
        prefill_mask[:, :, query_position, : query_position + 1] = 0
    for query_position in range(len(ids), max_seq):
        prefill_mask[:, :, query_position, 0] = 0
    final_token_selector = np.zeros((1, max_seq, 1), np_dt)
    final_token_selector[:, len(ids) - 1, :] = 1
    prefill_outputs = prefill.run(
        None,
        {
            "input_ids": np.array([padded_ids], np.int64),
            "attention_mask": prefill_mask,
            "final_token_selector": final_token_selector,
        },
    )
    prefill_names = [output.name for output in prefill.get_outputs()]
    prefill_logits = prefill_outputs[prefill_names.index("logits")]
    prefill_tok = int(np.argmax(prefill_logits[0, 0]))

    decoder_inputs = dec.get_inputs()
    prefill_output_descriptors = prefill.get_outputs()
    for kv_index in range(NL * 2):
        prefill_desc = prefill_output_descriptors[1 + kv_index]
        decoder_desc = decoder_inputs[3 + kv_index]
        if prefill_desc.type != decoder_desc.type or prefill_desc.shape != decoder_desc.shape:
            raise RuntimeError(
                "verify failed: prefill KV output is incompatible with decoder input "
                f"{kv_index} ({prefill_desc.type} {prefill_desc.shape} vs "
                f"{decoder_desc.type} {decoder_desc.shape})"
            )

    # Batched FP16 GEMMs may select a different reduction path than the shorter
    # unified prompt graph. The token and seeded-decode comparisons are the primary
    # checks; this bound catches large tensor divergence.
    numeric_tolerance = 5e-1 if np_dt == np.float16 else 1e-4
    logits_max_diff = float(
        np.max(
            np.abs(
                prefill_logits[0, 0].astype(np.float32)
                - unified_logits[0, -1].astype(np.float32)
            )
        )
    )
    logits_match = logits_max_diff <= numeric_tolerance
    kv_match = True
    kv_max_diff = 0.0
    prefill_kv = {}
    for i in range(NL):
        for kind in ("key", "value"):
            decode_name = f"past_key_values.{i}.{kind}"
            prefill_name = f"present.{i}.{kind}"
            value = prefill_outputs[prefill_names.index(prefill_name)]
            prefill_kv[decode_name] = value
            difference = float(
                np.max(
                    np.abs(
                        value[:, :, : len(ids), :].astype(np.float32)
                        - unified_kv[decode_name][
                            :, :, : len(ids), :
                        ].astype(np.float32)
                    )
                )
            )
            kv_max_diff = max(kv_max_diff, difference)
            kv_match = kv_match and difference <= numeric_tolerance

    unified_history = list(ids)
    unified_decode_tokens = []
    next_token = unified_tok
    for _ in range(2):
        decode_inputs = {
            "input_ids": np.array([next_token], np.int64),
            "past_seq_len": np.array([[len(unified_history)]], np.int32),
        }
        decode_inputs.update(unified_kv)
        decode_outputs = unified.run(None, decode_inputs)
        decode_logits = decode_outputs[unified_names.index("logits")]
        next_token = int(np.argmax(decode_logits[0, -1]))
        unified_decode_tokens.append(next_token)
        unified_history.append(next_token)
        for i in range(NL):
            unified_kv[f"past_key_values.{i}.key"] = decode_outputs[
                unified_names.index(f"present.{i}.key")
            ]
            unified_kv[f"past_key_values.{i}.value"] = decode_outputs[
                unified_names.index(f"present.{i}.value")
            ]

    task_decode_tokens = []
    next_token = task_tok
    task_decode_position_match = task_position_match
    fixed_decode_tokens = []
    fixed_decode_state = task_state.copy()
    fixed_next_token = task_tok
    fixed_decode_position_match = task_position_match
    fixed_decode_state_match = True
    fixed_decode_state_max_diff = 0.0
    for decode_index in range(2):
        task_outputs = task_model.run(
            None,
            {
                "input_ids": np.array([[next_token]], np.int64),
                "task_state": task_state,
            },
        )
        task_logits = task_outputs[0]
        task_state = task_outputs[1]
        next_token = int(np.argmax(task_logits[0, -1]))
        task_decode_tokens.append(next_token)
        expected_position = len(ids) + decode_index + 1
        task_decode_position_match = (
            task_decode_position_match
            and decode_task_position(task_state) == expected_position
        )
        fixed_outputs = task_decode.run(
            None,
            {
                "input_ids": np.array([[fixed_next_token]], np.int64),
                "task_state": fixed_decode_state,
            },
        )
        fixed_logits = fixed_outputs[0]
        fixed_decode_state = fixed_outputs[1]
        fixed_next_token = int(np.argmax(fixed_logits[0, -1]))
        fixed_decode_tokens.append(fixed_next_token)
        fixed_decode_position_match = (
            fixed_decode_position_match
            and decode_task_position(fixed_decode_state) == expected_position
        )
        state_difference = float(
            np.max(
                np.abs(
                    fixed_decode_state.astype(np.float32)
                    - task_state.astype(np.float32)
                )
            )
        )
        fixed_decode_state_max_diff = max(
            fixed_decode_state_max_diff,
            state_difference,
        )
        fixed_decode_state_match = (
            fixed_decode_state_match
            and state_difference <= numeric_tolerance
        )

    prefill_decode_inputs = {
        "hidden_state": emb.run(
            None,
            {"input_ids": np.array([[prefill_tok]], np.int64)},
        )[0].astype(np_dt),
        "position_id": np.array([len(ids)], np.int64),
        "attention_mask": np.where(
            np.arange(max_seq).reshape(1, 1, 1, max_seq) <= len(ids),
            np.array(0, dtype=np_dt),
            np.array(np.finfo(np_dt).min, dtype=np_dt),
        ),
    }
    prefill_decode_inputs.update(prefill_kv)
    prefill_decode_outputs = dec.run(None, prefill_decode_inputs)
    prefill_decode_hidden = prefill_decode_outputs[
        [output.name for output in dec.get_outputs()].index("hidden_out")
    ]
    prefill_decode_logits = head.run(
        None,
        {"hidden_state": prefill_decode_hidden.astype(np_dt)},
    )[0]
    prefill_decode_tok = int(np.argmax(prefill_decode_logits[0, -1]))

    with torch.no_grad():
        hf_logits = hf_model(torch.tensor([ids])).logits
    hf_tok = int(torch.argmax(hf_logits[0, -1]))
    hf_history = list(ids)
    hf_decode_tokens = []
    for token in [hf_tok, *unified_decode_tokens[:-1]]:
        hf_history.append(token)
        with torch.no_grad():
            logits = hf_model(torch.tensor([hf_history])).logits
        hf_decode_tokens.append(int(torch.argmax(logits[0, -1])))

    def safe_decode(t):
        try:
            return tok.decode([t]).encode("ascii", "backslashreplace").decode("ascii")
        except Exception:
            return "<decode-error>"

    split_match = onnx_tok == hf_tok
    unified_match = unified_tok == hf_tok
    prefill_match = prefill_tok == hf_tok
    task_match = task_tok == hf_tok
    prefill_decode_match = (
        bool(unified_decode_tokens)
        and prefill_decode_tok == unified_decode_tokens[0]
    )
    decode_match = unified_decode_tokens == hf_decode_tokens
    task_decode_match = task_decode_tokens == unified_decode_tokens
    fixed_decode_match = fixed_decode_tokens == task_decode_tokens

    print(f"\nVERIFY  prompt={text!r}")
    print(f"  ONNX next-token id={onnx_tok} ({safe_decode(onnx_tok)})")
    print(f"  Unified next-token id={unified_tok} ({safe_decode(unified_tok)})")
    print(f"  Prefill next-token id={prefill_tok} ({safe_decode(prefill_tok)})")
    print(f"  Task next-token id={task_tok} ({safe_decode(task_tok)})")
    print(f"  HF   next-token id={hf_tok} ({safe_decode(hf_tok)})")
    print(
        f"  MATCH split={split_match} unified={unified_match} "
        f"prefill={prefill_match} task={task_match} "
        f"logits={logits_match} kv={kv_match} "
        f"max_diffs=({logits_max_diff:.6g}, {kv_max_diff:.6g})"
    )
    print(
        "  TASK STATE MATCH: "
        f"kv={task_kv_match} position={task_decode_position_match} "
        f"kv_max_diff={task_kv_max_diff:.6g}"
    )
    for (
        dynamic_text,
        dynamic_length,
        dynamic_position,
        dynamic_task_token,
        dynamic_hf_token,
        dynamic_match,
    ) in task_dynamic_prompt_results:
        print(
            "  TASK DYNAMIC PROMPT: "
            f"text={dynamic_text!r} tokens={dynamic_length} "
            f"position={dynamic_position} task={dynamic_task_token} "
            f"hf={dynamic_hf_token} match={dynamic_match}"
        )
    print(
        "  PREFILL->DECODE MATCH: "
        f"{prefill_decode_match} prefill={prefill_decode_tok} "
        f"unified={unified_decode_tokens[0] if unified_decode_tokens else None}"
    )
    print(
        "  DECODE MATCH: "
        f"{decode_match} "
        f"unified={unified_decode_tokens} hf={hf_decode_tokens}"
    )
    print(
        "  TASK DECODE MATCH: "
        f"{task_decode_match} "
        f"task={task_decode_tokens} unified={unified_decode_tokens}"
    )
    print(
        "  FIXED TASK DECODE MATCH: "
        f"tokens={fixed_decode_match} state={fixed_decode_state_match} "
        f"position={fixed_decode_position_match} "
        f"fixed={fixed_decode_tokens} task={task_decode_tokens} "
        f"state_max_diff={fixed_decode_state_max_diff:.6g}"
    )
    if not (
        split_match
        and unified_match
        and prefill_match
        and task_match
        and logits_match
        and kv_match
        and task_kv_match
        and task_decode_position_match
        and task_dynamic_prompt_match
        and prefill_decode_match
        and decode_match
        and task_decode_match
        and fixed_decode_match
        and fixed_decode_state_match
        and fixed_decode_position_match
    ):
        raise RuntimeError(
            "verify failed: export output does not match Hugging Face "
            f"(split_match={split_match}, unified_match={unified_match}, "
            f"prefill_match={prefill_match}, task_match={task_match}, "
            f"logits_match={logits_match}, kv_match={kv_match}, "
            f"task_kv_match={task_kv_match}, "
            f"task_decode_position_match={task_decode_position_match}, "
            f"task_dynamic_prompt_match={task_dynamic_prompt_match}, "
            f"prefill_decode_match={prefill_decode_match}, "
            f"decode_match={decode_match}, "
            f"task_decode_match={task_decode_match}, "
            f"fixed_decode_match={fixed_decode_match}, "
            f"fixed_decode_state_match={fixed_decode_state_match}, "
            f"fixed_decode_position_match={fixed_decode_position_match})"
        )


if __name__ == "__main__":
    main()
