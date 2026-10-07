# Copyright (C) Microsoft Corporation. All rights reserved.

"""Whisper speech-to-text with the Windows ML Runtime Python projection.

Transcribe English speech from a WAV file with separate Whisper encoder and
decoder pipelines.

  runtime.tensor_from_audio makes a mono float32 waveform on a CPU target,
  and the sample resamples it to 16 kHz and computes (1, 80, 3000) log-mel
  features with NumPy. Model shapes are checked before build, the encoder
  turns off the same two ORT optimizers as C++ with set_session_config, and
  both stages must land on the same placement. The features and the encoder
  output (via to_numpy) reach the inference target with tensor_from_numpy.
  Decoding is greedy over an end-of-text padded (1, 128) buffer, running the
  full buffer each step (no KV cache) and reading the current logits row
  with region(), until end-of-text or --max-tokens (default 124). The C++
  sample computes features natively and adds microphone capture.

Run it
  python main.py <wav> --model-dir <dir> [--max-tokens N]
                 [--device cpu|gpu|npu] [--ep <name>]
                 [--performance|--efficiency] [--verbose]

Learn more (paths relative to this file)
  README.md
  ../../../../docs/Runtime/python-samples.md
  ../../../../docs/Runtime/tutorials/04-speech-to-language.md
  ../../../../docs/Runtime/providers.md
"""

from __future__ import annotations

import argparse
from contextlib import ExitStack
from pathlib import Path
import sys

_RUNTIME_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_RUNTIME_ROOT / "python"))

from winml_runtime_samples.entrypoint import run_entry_point  # noqa: E402
from winml_runtime_samples.inference import (  # noqa: E402
    add_inference_arguments,
    enter_execution_targets,
    execution_request,
    import_runtime,
    placement_provider_label,
    require_files,
    validate_stage_placement,
    validate_tensor_desc,
)
from winml_runtime_samples.media import import_numpy  # noqa: E402
from winml_runtime_samples.whisper import (  # noqa: E402
    ENGLISH_TOKEN,
    EOT_TOKEN,
    NO_TIMESTAMPS_TOKEN,
    SOT_TOKEN,
    TRANSCRIBE_TOKEN,
    WHISPER_MAX_DECODE_TOKENS,
    WHISPER_VOCAB_SIZE,
    WhisperTokenDecoder,
    load_vocabulary,
    load_wav_pcm,
    log_mel_spectrogram,
    resample_mono,
)


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Tensorize a WAV file, compute Whisper log-mel features, and run "
            "the encoder/decoder autoregressive loop."
        )
    )
    parser.add_argument("wav", type=Path, help="16-bit PCM or float32 WAV file.")
    parser.add_argument(
        "--model-dir",
        type=Path,
        required=True,
        help=(
            "Directory containing encoder_model.onnx, decoder_model.onnx, "
            "and vocab.json."
        ),
    )
    parser.add_argument(
        "--max-tokens",
        type=int,
        default=WHISPER_MAX_DECODE_TOKENS - 4,
        help="Maximum generated tokens after the four-token decoder prefix.",
    )
    add_inference_arguments(parser)
    args = parser.parse_args()
    if not 1 <= args.max_tokens <= WHISPER_MAX_DECODE_TOKENS - 4:
        parser.error(
            f"--max-tokens must be between 1 and "
            f"{WHISPER_MAX_DECODE_TOKENS - 4}"
        )
    return args


def main() -> int:
    args = _parse_args()
    request = execution_request(args)
    paths = {
        "WAV": args.wav,
        "encoder": args.model_dir / "encoder_model.onnx",
        "decoder": args.model_dir / "decoder_model.onnx",
        "vocabulary": args.model_dir / "vocab.json",
    }
    require_files(paths)

    runtime_api = import_runtime()
    np = import_numpy()
    vocabulary = load_vocabulary(paths["vocabulary"])
    wav = load_wav_pcm(paths["WAV"])

    print("=== Windows ML Runtime: Whisper speech-to-text ===\n")
    print(f"WAV: {paths['WAV']} ({wav.duration_seconds:.1f} seconds)")
    print(f"Model directory: {args.model_dir}")

    with ExitStack() as resources:
        # Runtime is the root factory for targets, models, pipeline builders, and
        # the target-bound tensor factories exposed by the Python projection.
        runtime = resources.enter_context(runtime_api.Runtime())
        audio_target = resources.enter_context(runtime.create_cpu_target())

        # tensor_from_audio wraps the Runtime audio tensor factory. The options
        # request a mono NT float32 waveform; resampling remains sample code.
        audio_options = runtime_api.AudioTensorOptions(
            data_type=runtime_api.TensorDataType.FLOAT32,
            layout=runtime_api.TensorLayout.NT,
            channels=1,
        )
        audio_tensor = resources.enter_context(
            runtime.tensor_from_audio(
                wav.data,
                sample_rate=wav.sample_rate,
                channels=wav.channels,
                sample_format=wav.sample_format,
                options=audio_options,
                target=audio_target,
            )
        )
        mono_samples = resample_mono(audio_tensor.to_numpy(), wav.sample_rate)
        print("[1/6] WAV decoded and tensorized to a mono float32 waveform.")

        mel = log_mel_spectrogram(mono_samples)
        if mel.shape != (1, 80, 3000):
            raise RuntimeError(f"Whisper mel tensor has unexpected shape: {mel.shape}")
        print("[2/6] Whisper log-mel spectrogram computed as [1,80,3000].")

        # Create the requested stage target. With --ep, the helper pins the stage
        # target to that registered provider and returns the hardware tensor target.
        stage_target, inference_tensor_target = enter_execution_targets(
            resources,
            runtime_api,
            runtime,
            request,
        )

        # Model schemas are available before Build; validate the fixed Whisper
        # shapes before relying on positional bindings in the loop below.
        encoder_source = resources.enter_context(
            runtime.load_model(str(paths["encoder"]))
        )
        decoder_source = resources.enter_context(
            runtime.load_model(str(paths["decoder"]))
        )
        with encoder_source.schema() as encoder_schema:
            if encoder_schema.input_count < 1 or encoder_schema.output_count < 1:
                raise RuntimeError(
                    "Whisper encoder must expose input and output ordinal 0."
                )
            encoder_input_type, encoder_input_shape = encoder_schema.input_desc(0)
            encoder_output_type, encoder_output_shape = encoder_schema.output_desc(0)
        if (
            encoder_input_type != runtime_api.TensorDataType.FLOAT32
            or encoder_input_shape != (1, 80, 3000)
            or encoder_output_type != runtime_api.TensorDataType.FLOAT32
            or encoder_output_shape != (1, 1500, 1024)
        ):
            raise RuntimeError(
                "Whisper encoder shape mismatch: "
                f"input={encoder_input_type.name} {encoder_input_shape}, "
                f"output={encoder_output_type.name} {encoder_output_shape}"
            )
        with decoder_source.schema() as decoder_schema:
            if decoder_schema.input_count < 2 or decoder_schema.output_count < 1:
                raise RuntimeError(
                    "Whisper decoder requires input ordinals 0 and 1 and output 0."
                )
            decoder_token_type, decoder_token_shape = decoder_schema.input_desc(0)
            decoder_state_type, decoder_state_shape = decoder_schema.input_desc(1)
        if (
            decoder_token_type != runtime_api.TensorDataType.INT64
            or decoder_token_shape != (1, WHISPER_MAX_DECODE_TOKENS)
            or decoder_state_type != runtime_api.TensorDataType.FLOAT32
            or decoder_state_shape != (1, 1500, 1024)
        ):
            raise RuntimeError(
                "Whisper decoder input shape mismatch: "
                f"tokens={decoder_token_type.name} {decoder_token_shape}, "
                f"state={decoder_state_type.name} {decoder_state_shape}"
            )
        encoder_model = encoder_source
        decoder_model = decoder_source
        print(
            f"[3/6] Encoder and decoder ready "
            f"for {request.device.upper()}."
        )

        # Each model is its own one-stage pipeline. Build validates that the
        # selected target can create a backend session for that stage.
        encoder_builder = resources.enter_context(
            runtime.create_pipeline_builder()
        )
        encoder_stage = resources.enter_context(
            encoder_builder.add_model_stage(
                encoder_model,
                stage_target,
                "whisper-encoder",
            )
        )
        # The Runtime publishes only outputs that are requested before the
        # pipeline is built.
        encoder_stage.request_output(0)
        # Whisper encoder setup disables two backend optimizer passes for this
        # model; the configuration names below are the public backend keys.
        with encoder_stage.ort_options() as options:
            options.set_session_config(
                "optimization.disable_specified_optimizers",
                "NhwcTransformer;ConvActivationFusion",
            )
        encoder_pipeline = resources.enter_context(encoder_builder.build())

        decoder_builder = resources.enter_context(
            runtime.create_pipeline_builder()
        )
        decoder_stage = resources.enter_context(
            decoder_builder.add_model_stage(
                decoder_model,
                stage_target,
                "whisper-decoder",
            )
        )
        decoder_stage.request_output(0)
        decoder_pipeline = resources.enter_context(decoder_builder.build())

        # Placement diagnostics confirm the built stages match the requested
        # hardware class and provider pinning instead of accepting a silent change.
        encoder_placement = validate_stage_placement(encoder_stage, request)
        decoder_placement = validate_stage_placement(decoder_stage, request)
        if encoder_placement != decoder_placement:
            raise RuntimeError("Whisper encoder and decoder resolved differently.")
        if args.verbose:
            print(
                "      placement: "
                f"device={encoder_placement.device}, "
                f"provider={placement_provider_label(request, encoder_placement)}"
            )
        print("[4/6] Independent encoder and decoder pipelines built.")

        # tensor_from_numpy creates the raw FLOAT32 mel tensor on the tensor
        # target associated with the requested execution path.
        mel_tensor = resources.enter_context(
            runtime.tensor_from_numpy(mel, target=inference_tensor_target)
        )
        validate_tensor_desc(
            mel_tensor.desc(),
            runtime_api.TensorDataType.FLOAT32,
            (1, 80, 3000),
            "Whisper encoder input",
        )
        # Bindings are positional: encoder input 0 is the mel spectrogram and
        # output 0 is the encoder state consumed by the decoder.
        encoder_stage.bind_input(0, mel_tensor)
        encoder_pipeline.run()
        with encoder_stage.output(0) as encoder_output:
            encoder_state_array = encoder_output.to_numpy()
        if (
            encoder_state_array.dtype != np.float32
            or encoder_state_array.shape != (1, 1500, 1024)
        ):
            raise RuntimeError(
                "Whisper encoder output ordinal 0 shape mismatch: "
                f"{encoder_state_array.dtype} {encoder_state_array.shape}"
            )
        # Copy the encoder output into a fresh tensor whose lifetime is separate
        # from the encoder stage output object.
        encoder_state = resources.enter_context(
            runtime.tensor_from_numpy(
                encoder_state_array,
                target=inference_tensor_target,
            )
        )
        print(
            "[5/6] Encoder output copied into decoder-state tensor "
            f"{encoder_state_array.shape}."
        )

        tokens = [
            SOT_TOKEN,
            ENGLISH_TOKEN,
            TRANSCRIBE_TOKEN,
            NO_TIMESTAMPS_TOKEN,
        ]
        generated_tokens: list[int] = []
        text_decoder = WhisperTokenDecoder(vocabulary)
        token_buffer = np.full(
            (1, WHISPER_MAX_DECODE_TOKENS),
            EOT_TOKEN,
            dtype=np.int64,
        )

        print("[6/6] Decoding one token at a time.\n")
        print("Transcription:\n\n  >> ", end="", flush=True)
        stop_reason = "MAX_TOKENS"
        for _step in range(args.max_tokens):
            token_buffer.fill(EOT_TOKEN)
            token_buffer[0, : len(tokens)] = tokens
            # Rebind a fixed-shape token tensor and the stable encoder state for
            # each autoregressive decoder step.
            with runtime.tensor_from_numpy(
                token_buffer,
                target=inference_tensor_target,
            ) as token_tensor:
                decoder_stage.bind_input(0, token_tensor)
                decoder_stage.bind_input(1, encoder_state)
                decoder_pipeline.run()

            current_position = len(tokens) - 1
            with decoder_stage.output(0) as decoder_output:
                logits_type, logits_shape = decoder_output.desc()
                if (
                    logits_type != runtime_api.TensorDataType.FLOAT32
                    or len(logits_shape) != 3
                    or logits_shape[0] != 1
                    or logits_shape[2] != WHISPER_VOCAB_SIZE
                    or logits_shape[1] <= current_position
                ):
                    raise RuntimeError(
                        "Whisper decoder output ordinal 0 logits shape mismatch: "
                        f"{logits_type.name} {logits_shape}"
                    )
                # Region narrows the readback to the current logits row so the
                # sample does not copy the full [1,sequence,vocab] output.
                with decoder_output.region(
                    (0, current_position, 0),
                    (1, 1, WHISPER_VOCAB_SIZE),
                    target=audio_target,
                ) as position_output:
                    position_logits = position_output.to_numpy().reshape(-1)
            next_token = int(np.argmax(position_logits))
            if next_token == EOT_TOKEN:
                stop_reason = "EOT"
                break

            fragment = text_decoder.decode_token(next_token)
            print(fragment, end="", flush=True)
            generated_tokens.append(next_token)
            tokens.append(next_token)
            if len(tokens) >= WHISPER_MAX_DECODE_TOKENS:
                stop_reason = "CAPACITY"
                break

        final_fragment = text_decoder.finish()
        print(final_fragment, end="", flush=True)
        print("\n\nToken IDs:", *generated_tokens)
        if stop_reason == "EOT":
            print("\n=== Transcription complete (EOT). ===")
        elif stop_reason == "CAPACITY":
            print("\n=== Transcription truncated at decoder capacity. ===")
        else:
            print("\n=== Transcription truncated at --max-tokens. ===")

    return 0


if __name__ == "__main__":
    run_entry_point(main)
