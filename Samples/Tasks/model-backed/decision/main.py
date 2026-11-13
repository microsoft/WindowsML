# Copyright (C) Microsoft Corporation. All rights reserved.

"""Typed decisions: answer a choice question and print its probability distribution.

The task creates the model's own tokenizer from the model directory, so the
app passes none.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "python"))

from winml_task_samples.entrypoint import run_entry_point  # noqa: E402
from winml_task_samples.model_tasks import (  # noqa: E402
    add_program_cache_arguments,
    is_not_supported,
    program_cache_options,
)
from windowsml.runtime import ExecutionTargetKind, Runtime, WinMLError  # noqa: E402
from windowsml.tasks import Tasks  # noqa: E402

try:
    from windowsml.tasks import DecisionOption, DecisionQuestion, DecisionQuestionKind
except ImportError as error:
    raise SystemExit(
        "error: the installed windowsml package does not include typed decisions."
    ) from error


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", type=Path, help="Decision model directory.")
    parser.add_argument("--state", default="I was charged twice for my subscription.")
    parser.add_argument("--question", default="Which team should handle this support ticket?")
    parser.add_argument("--options", nargs="+", default=["billing", "technical"])
    add_program_cache_arguments(parser)
    args = parser.parse_args()
    if any(not label for label in args.options) or len(set(args.options)) != len(args.options):
        parser.error("Option labels must be nonempty and unique.")
    cache_options = program_cache_options(parser, args)

    with (
        Runtime() as runtime,
        runtime.create_target(ExecutionTargetKind.GPU) as target,
        runtime.load_model(str(args.model)) as model,
        Tasks(runtime) as tasks,
    ):
        # Without a tokenizer, the task creates the model's own.
        try:
            task = tasks.create_decision_task(target, model, **cache_options)
        except WinMLError as error:
            if is_not_supported(error):
                raise RuntimeError(
                    "Windows ML cannot run this model for typed decisions on this GPU "
                    "(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). Use a supported decision "
                    "model directory and a GPU execution target."
                ) from error
            raise
        with task, task.create_session() as session:
            print(f"Calibrated probabilities: {task.capabilities.calibrated}")
            print(f"State: {args.state}\nQuestion: {args.question}")
            result = session.decide(
                args.state,
                [DecisionQuestion(
                    DecisionQuestionKind.CHOICE,
                    args.question,
                    tuple(DecisionOption(label) for label in args.options),
                )],
            )

    # Results are immutable Python snapshots that outlive every native owner.
    answer, = result.answers
    for label, probability in zip(args.options, answer.probabilities):
        print(f"{label}: {probability:.4f}")
    print(
        f"Selected: {args.options[answer.selected_option]}; "
        f"confidence: {answer.confidence:.4f}; "
        f"recommendation: {answer.recommendation.name}"
    )
    print(f"Input tokens: {result.input_token_count}")
    return 0


if __name__ == "__main__":
    run_entry_point(main)
