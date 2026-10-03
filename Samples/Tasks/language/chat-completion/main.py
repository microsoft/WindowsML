# Copyright (C) Microsoft Corporation. All rights reserved.

"""Hold a two-turn conversation through the Windows ML Chat Completion Task.

Ask a question, then a follow-up that depends on the answer. The app keeps
the message history and sends all of it on each turn.

  A greedy Text Generation Task on a CPU target supplies the default session
  for tasks.create_chat_completion_task, so tokens it has already evaluated
  carry over between turns. Each turn calls chat.stream with the full
  history; the session applies the chat template, events carry content
  deltas, and stream.result gives the reply, token counts, and finish
  reason. When a prompt starts with tokens the session already holds, only
  the rest is evaluated; prompt_cache_use reports how many were reused.

Run it
  python main.py <model> [--backend auto|ort|llama] [--tokenizer <path>]
                 [--prompt <text>] [--follow-up <text>] [--max-new-tokens <count>]

Learn more (paths relative to this file)
  README.md
  ../../../../docs/Tasks/task-lifecycle.md
  ../../../../docs/api-reference/IWinMLChatCompletionTask.md
"""

from __future__ import annotations

import argparse
from contextlib import ExitStack
from pathlib import Path
import sys

_TASKS_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_TASKS_ROOT / "python"))

from windowsml.runtime import ChatRole  # noqa: E402
from windowsml.tasks import (  # noqa: E402
    ChatCompletionEventType,
    ChatCompletionMessage,
    ChatCompletionRequest,
    TextGenerationOptions,
)
from winml_task_samples.entrypoint import run_entry_point  # noqa: E402
from winml_task_samples.text_generation import open_text_generation_task  # noqa: E402


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Hold a conversation with a Windows ML Chat Completion Task."
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
    parser.add_argument("--prompt", default="Name three primary colors.")
    parser.add_argument("--follow-up", default="Which of those is the color of the sky?")
    parser.add_argument(
        "--max-new-tokens",
        type=int,
        default=48,
        help="Maximum number of tokens in each reply.",
    )
    args = parser.parse_args()
    if args.max_new_tokens <= 0:
        parser.error("--max-new-tokens must be positive")
    return args


def main() -> int:
    args = _parse_args()

    with ExitStack() as resources:
        # The chat Task runs on a Text Generation Task built on the CPU with
        # greedy decoding, so the replies are the same from run to run.
        tasks, tokenizer, text_task = open_text_generation_task(
            resources, args.model, args.tokenizer or args.model.parent, args.backend
        )
        chat = resources.enter_context(
            tasks.create_chat_completion_task(
                tokenizer, default_text_generation_session=text_task.default_session
            )
        )
        options = TextGenerationOptions(max_new_tokens=args.max_new_tokens)

        history: list[ChatCompletionMessage] = []
        for question in (args.prompt, args.follow_up):
            history.append(ChatCompletionMessage(role=ChatRole.USER, content=question))
            print(f"User: {question}")
            print("Assistant: ", end="", flush=True)
            with chat.stream(ChatCompletionRequest(messages=tuple(history)), options) as stream:
                for event in stream:
                    event.raise_for_error()
                    if event.type == ChatCompletionEventType.CONTENT_DELTA:
                        print(event.text, end="", flush=True)
                print()
                # The result stays readable until the stream closes.
                result = stream.result
                result.raise_for_error()
                reused = result.prompt_cache_use.cached_prompt_token_count
            print(
                f"  Tokens: prompt={result.prompt_token_count} (reused={reused}) "
                f"completion={result.completion_token_count}"
            )
            print(f"  Finished because: {result.finish_reason.name}")
            history.append(
                ChatCompletionMessage(role=ChatRole.ASSISTANT, content=result.content)
            )
    return 0


if __name__ == "__main__":
    run_entry_point(main)
