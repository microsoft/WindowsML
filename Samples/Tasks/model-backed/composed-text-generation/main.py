# Copyright (C) Microsoft Corporation. All rights reserved.

"""Bring your own model: describe a composition of loaded models and stream a reply.

The app loads the exported Qwen ONNX decoder and its tokenizer and describes
them as a UNIFIED composition. The language text-generation sample wires the
same model by hand.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "python"))

from winml_task_samples.entrypoint import run_entry_point  # noqa: E402
from winml_task_samples.model_tasks import DEFAULT_CHAT_PROMPT, stream_chat_reply  # noqa: E402
from winml_task_samples.text_generation import (  # noqa: E402
    _read_declared_sequence_capacity,
)
from windowsml.runtime import ExecutionTargetKind, Runtime, Tokenizer  # noqa: E402
from windowsml.tasks import (  # noqa: E402
    Tasks,
    TextGenerationComposedModel,
    TextGenerationComposedStage,
    TextGenerationCompositionKind,
    TextGenerationOutputKind,
)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", type=Path, help="The exported task_model.onnx.")
    parser.add_argument("tokenizer", type=Path, help="The model's tokenizer.json.")
    parser.add_argument("--prompt", default=DEFAULT_CHAT_PROMPT)
    parser.add_argument("--max-new-tokens", type=int, default=64)
    parser.add_argument("--device", choices=("cpu", "gpu"), default="cpu")
    args = parser.parse_args()
    if args.max_new_tokens <= 0:
        parser.error("--max-new-tokens must be positive.")

    with (
        Runtime() as runtime,
        (
            runtime.create_target(ExecutionTargetKind.GPU)
            if args.device == "gpu"
            else runtime.create_cpu_target()
        ) as target,
        runtime.load_model(str(args.model)) as model,
        Tokenizer.from_file(str(args.tokenizer)) as tokenizer,
        Tasks(runtime) as tasks,
        tasks.create_text_generation_configuration() as configuration,
    ):
        # A composition has no tokenizer files, so set the tokenizer first.
        configuration.set_tokenizer(tokenizer)

        # The exported decoder takes token IDs at input 0 and produces logits at
        # output 0. Input 1 and output 1 are its key/value state, which the stage
        # feeds back after each step.
        configuration.set_composition(
            target,
            TextGenerationComposedModel(
                TextGenerationCompositionKind.UNIFIED,
                model=TextGenerationComposedStage(
                    model,
                    token_input_index=0,
                    output_index=0,
                    output_kind=TextGenerationOutputKind.LOGITS,
                    state_pairs=((1, 1),),
                ),
            ),
        )

        # The export fixes its sequence capacity in the shape of the state input.
        configuration.set_logical_capacity(_read_declared_sequence_capacity(model))

        # The task uses the same tokenizer that the configuration holds.
        with (
            tasks.create_text_generation_task(tokenizer) as task,
            task.create_session(configuration) as session,
        ):
            stream_chat_reply(session, tokenizer, args.prompt, args.max_new_tokens)
    return 0


if __name__ == "__main__":
    run_entry_point(main)
