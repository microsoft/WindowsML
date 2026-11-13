# Copyright (C) Microsoft Corporation. All rights reserved.

"""Helpers shared by the model-backed Task samples."""

from __future__ import annotations

import argparse
from pathlib import Path
from typing import Any

from windowsml.runtime import (
    ChatCompletionMessage,
    ChatCompletionRequest,
    ChatRole,
    Tokenizer,
    WinMLError,
)
from windowsml.tasks import TextGenerationOptions, TextGenerationResult, TextGenerationSession

from winml_task_samples.entrypoint import FragmentPrinter

DEFAULT_CHAT_PROMPT = "Write one sentence about the ocean."

# HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)
_HRESULT_NOT_SUPPORTED = 0x80070032


def is_not_supported(error: WinMLError) -> bool:
    """Whether Windows ML reported that it does not support the request."""

    return (error.hresult & 0xFFFFFFFF) == _HRESULT_NOT_SUPPORTED


def add_program_cache_arguments(parser: argparse.ArgumentParser) -> None:
    """Add the program cache options that every Models-backed Task accepts."""

    parser.add_argument(
        "--program-cache", type=Path, help="Directory that keeps compiled programs."
    )
    parser.add_argument(
        "--cache-weights",
        action="store_true",
        help="Also keep the weights prepared for each program in the program cache.",
    )


def program_cache_options(
    parser: argparse.ArgumentParser, args: argparse.Namespace
) -> dict[str, Any]:
    """Keyword arguments for set_model or create_decision_task.

    A program cache keeps the programs that sessions compile for the GPU, so
    later runs start faster. With --cache-weights it also keeps the weights
    prepared for each program.
    """

    if args.cache_weights and args.program_cache is None:
        parser.error("--cache-weights requires --program-cache.")
    return {
        "compiled_program_directory": str(args.program_cache) if args.program_cache else None,
        "store_compiled_program_weights": args.cache_weights,
    }


def stream_chat_reply(
    session: TextGenerationSession,
    tokenizer: Tokenizer,
    prompt: str,
    max_new_tokens: int,
) -> TextGenerationResult:
    """Send the prompt as one user turn and stream the reply.

    The model's chat template formats the turn, so an instruct model ends its
    reply on its own.
    """

    request = ChatCompletionRequest(
        messages=(ChatCompletionMessage(role=ChatRole.USER, content=prompt),),
    )
    print(f"Prompt: {prompt}")
    with (
        tokenizer.format_conversation(request) as formatted,
        session.generate_tokens(
            list(formatted.token_ids),
            TextGenerationOptions(max_new_tokens=max_new_tokens),
        ) as stream,
    ):
        fragments = FragmentPrinter("Reply: ")
        for update in stream:
            fragments(update.fragment)
        result = stream.result
        result.raise_for_error()
    print()
    fragments.require_matches(result.text, "Stream and final text differ.")
    print(
        f"Tokens: prompt={result.prompt_token_count} "
        f"generated={result.generated_token_count}"
    )
    print(f"Finished because: {result.finish_reason.name}")
    return result
