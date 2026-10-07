# Copyright (C) Microsoft Corporation. All rights reserved.

"""Argument and console helpers shared by the Python Runtime samples.

This is entry-point plumbing: it gathers common options and prints diagnostics
while Runtime object creation stays in the sample-specific modules.
"""

from __future__ import annotations

import argparse
from pathlib import Path
from typing import Callable

from .inference import pinned_execution_provider
from .language import (
    Backend,
    Generation,
    LanguageRequest,
    SplitLanguageRunner,
    UnifiedLanguageRunner,
)


def resolve_chat_mode(backend: Backend, requested_mode: str | None) -> str:
    """Resolve an omitted chat mode without silently changing an explicit mode."""

    if requested_mode not in {None, "unified", "split"}:
        raise ValueError(f"Unsupported Python chat mode: {requested_mode}.")
    if backend is Backend.LLAMA and requested_mode == "split":
        raise ValueError("--backend llama supports only --mode unified.")
    return requested_mode or "unified"


def add_execution_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument(
        "--backend",
        choices=[backend.value for backend in Backend],
        default=Backend.ORT.value,
        help="ort for ONNX/ORT models, llama for GGUF models.",
    )
    parser.add_argument(
        "--device",
        choices=("cpu", "gpu", "npu"),
        default="cpu",
        help="Requested execution target.",
    )
    parser.add_argument("--ep", help="Optional ORT execution-provider name.")
    parser.add_argument(
        "--tokenizer",
        type=Path,
        help="Optional tokenizer directory or file for a unified artifact.",
    )
    parser.add_argument(
        "--context",
        type=int,
        default=0,
        help=(
            "Unified-artifact sequence-capacity hint; zero uses the capacity "
            "the model's state declares, or the backend default."
        ),
    )
    policy = parser.add_mutually_exclusive_group()
    policy.add_argument(
        "--performance",
        action="store_true",
        help="Request the Runtime performance target policy.",
    )
    policy.add_argument(
        "--efficiency",
        action="store_true",
        help="Request the Runtime efficiency target policy.",
    )
    parser.add_argument(
        "--raw",
        action="store_true",
        help="Skip the model chat template and submit text directly.",
    )


def language_request(args: argparse.Namespace) -> LanguageRequest:
    return LanguageRequest(
        backend=Backend(args.backend),
        device=args.device,
        ep=pinned_execution_provider(args.ep, args.device),
        tokenizer_source=args.tokenizer,
        context_capacity=args.context,
        target_policy=(
            "performance"
            if args.performance
            else "efficiency"
            if args.efficiency
            else None
        ),
    )


def reject_bundled_model_on_npu(request: LanguageRequest, custom_model: object) -> None:
    """Refuse the NPU for the bundled language model export, which targets CPU and GPU."""

    if request.backend is Backend.ORT and request.device == "npu" and not custom_model:
        raise ValueError(
            "The bundled language model export is built for CPU and GPU. To try "
            "an NPU, pass --model with a model prepared for that NPU."
        )


def print_generation(generation: Generation, *, stream: bool) -> None:
    if not stream:
        print(generation.text, end="")
    print()
    first = (
        f"{generation.time_to_first_token_seconds:.3f}s"
        if generation.time_to_first_token_seconds is not None
        else "n/a"
    )
    stop = generation.stop_reason or "unknown"
    print(
        f"[tokens={generation.token_count}, first={first}, "
        f"throughput={generation.tokens_per_second:.2f}/s, stop={stop}]"
    )


def print_diagnostics(
    runner: UnifiedLanguageRunner | SplitLanguageRunner,
) -> None:
    """Print public stage placement diagnostics, or explain unavailability."""

    diagnostics = runner.ort_diagnostics()
    if diagnostics is None:
        print("Diagnostics: unavailable for the selected Runtime stage.")
        return
    requested, selected, pinned = diagnostics
    print(
        "Diagnostics: "
        f"requested={requested or 'default'}, "
        f"selected={selected or 'default'}, pinned={pinned}"
    )


def run_chat(
    runner: UnifiedLanguageRunner | SplitLanguageRunner,
    *,
    prompt: str | None,
    max_tokens: int,
    raw: bool,
    stream: bool,
    emit: Callable[[str], None] = print,
) -> None:
    """Run one prompt or the same reset/new/quit interactive contract as C++ chat."""

    def generate_turn(text: str) -> None:
        generation = runner.generate(
            text,
            max_new_tokens=max_tokens,
            raw=raw,
            on_fragment=(
                (lambda fragment: print(fragment, end="", flush=True))
                if stream
                else None
            ),
        )
        print_generation(generation, stream=stream)

    if prompt is not None:
        emit(f"> {prompt}")
        generate_turn(prompt)
        return

    emit('Type a message. Commands: "new" clears the conversation, "quit" exits.')
    while True:
        try:
            line = input("> ").strip()
        except EOFError:
            break
        if not line:
            continue
        if line in {"quit", "exit"}:
            break
        if line in {"new", "reset"}:
            runner.reset()
            emit("(context cleared)")
            continue
        generate_turn(line)
