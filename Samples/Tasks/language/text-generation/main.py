# Copyright (C) Microsoft Corporation. All rights reserved.

"""Stream completion text from a Windows ML Text Generation Task.

Continue a prompt, or answer it as a chat turn with --chat, and stream the
text from an ONNX/ORT or GGUF model.

  The app builds the Runtime pipeline and tokenizer on a CPU target (or GPU
  for GGUF with --device gpu); the Task runs the generation loop. An
  ONNX/ORT stage declares a past/present state pair and capacity, while a
  GGUF model keeps its own state and tokenizer. configure_unified names the
  token input, logits output, state owner, and token target; --speculative
  adds draft settings on the GGUF stage and the configuration. The default
  session's generate_text (generate_tokens for --chat) yields fragments,
  and the streamed text must match stream.result. Decoding is always
  greedy; sampling options, cancellation, and the hybrid backend are in the
  C++ sample.

Run it
  python main.py <model> [--backend auto|ort|llama] [--tokenizer <path>]
                 [--prompt <text>] [--max-new-tokens <count>] [--chat]
                 [--device cpu|gpu]
                 [--speculative none|model|draft-model|prompt-lookup]
                 [--draft-tokens <count>] [--draft-model <gguf>]

Learn more (paths relative to this file)
  README.md
  ../../../../docs/Tasks/task-lifecycle.md
  ../../../../docs/api-reference/IWinMLTasks.md
  ../../../../docs/api-reference/IWinMLTextGenerationTask.md
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

_TASKS_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_TASKS_ROOT / "python"))

from winml_task_samples.entrypoint import FragmentPrinter, run_entry_point  # noqa: E402
from winml_task_samples.text_generation import generate_text  # noqa: E402


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate text with a Windows ML Text Generation Task."
    )
    parser.add_argument(
        "model",
        type=Path,
        help="Task-compatible ONNX/ORT or GGUF decoder.",
    )
    parser.add_argument(
        "--backend",
        choices=("auto", "ort", "llama"),
        default="auto",
        help="Execution backend. Auto selects llama for GGUF and ORT otherwise.",
    )
    parser.add_argument(
        "--tokenizer",
        type=Path,
        help="Tokenizer file or directory. Defaults to the model directory.",
    )
    parser.add_argument(
        "--prompt",
        default="The sky is often",
        help="Text to submit to the Task.",
    )
    parser.add_argument(
        "--max-new-tokens",
        type=int,
        default=32,
        help="Maximum number of tokens to generate.",
    )
    parser.add_argument(
        "--chat",
        action="store_true",
        help="Send the prompt as a user turn in the model's chat template.",
    )
    parser.add_argument(
        "--device",
        choices=("cpu", "gpu"),
        default="cpu",
        help="Place a GGUF model on the CPU or GPU.",
    )
    parser.add_argument(
        "--speculative",
        choices=("none", "model", "draft-model", "prompt-lookup"),
        default="none",
        help=(
            "Propose several tokens per step and verify them in one pass. "
            "model uses the model's own draft predictor (built-in MTP/NextN "
            "layers, or a DFlash/DFlash2/DSpark companion given as --draft-model), "
            "draft-model a smaller GGUF model with the same tokenizer, "
            "prompt-lookup n-grams of earlier text."
        ),
    )
    parser.add_argument(
        "--draft-tokens",
        type=int,
        help="Most draft tokens per step, 1..16 (default 3).",
    )
    parser.add_argument(
        "--draft-model",
        type=Path,
        help="Required smaller model for draft-model; optional block draft companion for model.",
    )
    args = parser.parse_args()
    if args.max_new_tokens <= 0:
        parser.error("--max-new-tokens must be positive")
    if args.draft_tokens is not None and not 1 <= args.draft_tokens <= 16:
        parser.error("--draft-tokens must be in 1..16")
    return args


def main() -> int:
    args = _parse_args()
    tokenizer = args.tokenizer or args.model.parent

    # generate_text builds the Runtime pipeline, creates IWinMLTasks and a typed
    # session, then drains the Task pull stream before returning the result.
    fragments = FragmentPrinter("Generated text: ")
    result = generate_text(
        args.model,
        tokenizer,
        args.prompt,
        args.max_new_tokens,
        fragments,
        args.backend,
        chat=args.chat,
        device=args.device,
        speculative=args.speculative,
        # Zero lets the helper choose its default draft token count.
        draft_tokens=args.draft_tokens or 0,
        draft_model=args.draft_model,
    )
    print()
    # The final result is the authoritative completed text and status.
    fragments.require_matches(
        result.text,
        "Streamed text did not match the completed Task result.",
    )
    print(
        f"Prompt tokens: {result.prompt_token_count}; "
        f"generated tokens: {result.generated_token_count}; "
        f"finish reason: {result.finish_reason.name}"
    )
    statistics = result.speculative_statistics
    if args.speculative != "none" and statistics is not None:
        print(
            f"Speculative decoding: {statistics.method.name} "
            f"steps={statistics.verification_step_count} "
            f"drafted={statistics.draft_token_count} "
            f"accepted={statistics.accepted_draft_token_count} "
            f"acceptance={statistics.acceptance_rate:.1%}"
        )
    return 0


if __name__ == "__main__":
    run_entry_point(main)
