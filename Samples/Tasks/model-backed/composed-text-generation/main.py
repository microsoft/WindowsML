# Copyright (C) Microsoft Corporation. All rights reserved.

"""Bring your own model: describe a composition of loaded models and stream a reply.

The app loads the exported Qwen ONNX decoder and its tokenizer and describes
them as a UNIFIED composition. The language text-generation sample wires the
same model by hand.
"""

from __future__ import annotations

import argparse
from contextlib import nullcontext
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "python"))
sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "Runtime" / "python"))

from winml_runtime_samples.inference import canonical_execution_provider  # noqa: E402
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
    parser.add_argument(
        "--ep",
        help="Execution provider to pin on the device, such as webgpu, trtrtx or a "
        "provider's catalog name. --device gpu pins webgpu by default.",
    )
    args = parser.parse_args()
    if args.max_new_tokens <= 0:
        parser.error("--max-new-tokens must be positive.")
    # WebGPU is the default GPU provider because it works across GPU vendors,
    # and the Runtime acquires it if it isn't installed. --ep pins another
    # provider, such as trtrtx for TensorRT for RTX on an NVIDIA RTX GPU.
    provider = canonical_execution_provider(
        args.ep or ("webgpu" if args.device == "gpu" else None)
    )
    kind = ExecutionTargetKind.GPU if args.device == "gpu" else ExecutionTargetKind.CPU

    with (
        Runtime() as runtime,
        (
            runtime.create_target(kind)
            if args.device == "gpu"
            else runtime.create_cpu_target()
        ) as device_target,
        # Pin the provider on the device's target. The Runtime acquires a
        # pinned provider when it builds the composition. The composition needs
        # the device's target to create its token tensors.
        (
            runtime.create_ort_execution_target(provider, kind, device_target)
            if provider is not None
            else nullcontext(device_target)
        ) as target,
        runtime.load_model(str(args.model)) as model,
        Tokenizer.from_file(str(args.tokenizer)) as tokenizer,
        Tasks(runtime) as tasks,
        tasks.create_text_generation_configuration() as configuration,
    ):
        if provider is not None:
            print(f"Pinned execution provider: {provider}")

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
