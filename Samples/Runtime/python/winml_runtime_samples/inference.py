# Copyright (C) Microsoft Corporation. All rights reserved.

"""Runtime execution-target helpers shared by the Python inference samples.

This module wraps target creation, optional provider pinning, placement
diagnostics, and model compilation calls used by the Python Runtime samples.
"""

from __future__ import annotations

import argparse
from contextlib import contextmanager
from dataclasses import dataclass
import importlib
from pathlib import Path
import tempfile
from typing import Any, Iterator, Mapping


@dataclass(frozen=True)
class ExecutionRequest:
    """User request for hardware class, optional provider, and adapter policy."""

    device: str
    ep: str | None = None
    target_policy: str | None = None


@dataclass(frozen=True)
class Placement:
    """Resolved stage placement reported by the Runtime projection."""

    device: str
    selected_provider: str | None


_EXECUTION_PROVIDER_ALIASES = {
    "migraphx": "MIGraphXExecutionProvider",
    "nvtensorrtrtx": "NvTensorRTRTXExecutionProvider",
    "openvino": "OpenVINOExecutionProvider",
    "qnn": "QNNExecutionProvider",
    "vitisai": "VitisAIExecutionProvider",
}

# Runtime-owned providers are reached through the default GPU target rather than
# pinned as app-registered providers, matching the C++ samples.
_RUNTIME_OWNED_PROVIDERS = {"webgpu", "webgpuexecutionprovider"}


def import_runtime() -> Any:
    """Import the Runtime projection only when a sample starts running."""

    try:
        return importlib.import_module("windowsml.runtime")
    except ImportError as error:
        raise RuntimeError(
            "This sample requires a windowsml package that exposes windowsml.runtime. "
            "Install the matching Windows ML Runtime package in this Python environment."
        ) from error


def add_inference_arguments(parser: argparse.ArgumentParser) -> None:
    """Add common device, provider, adapter-policy, and diagnostics arguments."""

    parser.add_argument(
        "--device",
        choices=("cpu", "gpu", "npu"),
        default="cpu",
        help="Requested execution target.",
    )
    parser.add_argument(
        "--ep",
        help="Catalog short name or canonical execution-provider name to pin.",
    )
    policy = parser.add_mutually_exclusive_group()
    policy.add_argument(
        "--performance",
        action="store_true",
        help="Prefer a performance-oriented adapter.",
    )
    policy.add_argument(
        "--efficiency",
        action="store_true",
        help="Prefer an efficiency-oriented adapter.",
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="Print resolved Runtime placement information.",
    )


def execution_request(args: argparse.Namespace) -> ExecutionRequest:
    """Convert parsed arguments into one validated Runtime placement request."""

    request = ExecutionRequest(
        device=args.device.casefold(),
        ep=pinned_execution_provider(args.ep, args.device),
        target_policy=(
            "performance"
            if args.performance
            else "efficiency"
            if args.efficiency
            else None
        ),
    )
    validate_execution_request(request)
    return request


def require_files(paths: Mapping[str, Path]) -> None:
    """Report missing sample artifacts with consistent actionable names."""

    for description, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(f"{description} file was not found: {path}")


def validate_execution_request(request: ExecutionRequest) -> None:
    """Reject unsupported device/provider combinations before models are opened."""

    if request.device not in {"cpu", "gpu", "npu"}:
        raise ValueError("--device must be cpu, gpu, or npu.")
    if request.target_policy not in {None, "performance", "efficiency"}:
        raise ValueError("target policy must be performance or efficiency.")
    if request.ep is not None and not request.ep.strip():
        raise ValueError("--ep must not be empty.")


def canonical_execution_provider(provider: str | None) -> str | None:
    """Expand provider-guide short names to backend provider identifiers."""

    if provider is None:
        return None
    return _EXECUTION_PROVIDER_ALIASES.get(provider.casefold(), provider)


def pinned_execution_provider(provider: str | None, device: str) -> str | None:
    """Return the provider to pin, or None when the Runtime owns the target."""

    if provider is not None and provider.strip().casefold() in _RUNTIME_OWNED_PROVIDERS:
        if device.casefold() != "gpu":
            raise ValueError(f"--ep {provider} supports --device gpu only.")
        print("WebGPU uses the Runtime-owned default GPU target.")
        return None
    return canonical_execution_provider(provider)


def _target_kind(runtime_api: Any, device: str) -> Any:
    return {
        "cpu": runtime_api.ExecutionTargetKind.CPU,
        "gpu": runtime_api.ExecutionTargetKind.GPU,
        "npu": runtime_api.ExecutionTargetKind.NPU,
    }[device]


def _target_preference(runtime_api: Any, policy: str | None) -> Any:
    return {
        None: runtime_api.ExecutionTargetPreference.DEFAULT,
        "performance": runtime_api.ExecutionTargetPreference.PERFORMANCE,
        "efficiency": runtime_api.ExecutionTargetPreference.EFFICIENCY,
    }[policy]


def create_execution_targets(
    runtime_api: Any,
    runtime: Any,
    request: ExecutionRequest,
) -> tuple[Any, Any]:
    """Create the stage target and its underlying tensor-allocation target.

    A provider request is pinned to the requested device class; failures surface
    during target creation or pipeline build instead of falling back silently.
    """

    validate_execution_request(request)
    kind = _target_kind(runtime_api, request.device)
    preference = _target_preference(runtime_api, request.target_policy)

    if request.ep:
        provider = canonical_execution_provider(request.ep)
        # Without a preference, pass no hardware target so the Runtime pairs the
        # provider with one of its own devices. On a machine with two GPUs, the
        # default GPU may not be one the provider supports.
        hardware_target = (
            runtime.create_target(kind, preference)
            if request.target_policy is not None and request.device != "npu"
            else None
        )
        stage_target = runtime.create_ort_execution_target(
            provider,
            kind,
            hardware_target,
        )
        if hardware_target is None:
            # Host data goes into CPU tensors, and the Runtime moves it to the
            # stage's device.
            return stage_target, runtime.create_cpu_target()
        return stage_target, hardware_target

    if request.device == "cpu" and request.target_policy is None:
        hardware_target = runtime.create_cpu_target()
    else:
        hardware_target = runtime.create_target(kind, preference)
    return hardware_target, hardware_target


def enter_execution_targets(
    resources: Any,
    runtime_api: Any,
    runtime: Any,
    request: ExecutionRequest,
) -> tuple[Any, Any]:
    """Create and retain stage and tensor targets in one ExitStack."""

    raw_stage_target, raw_tensor_target = create_execution_targets(
        runtime_api,
        runtime,
        request,
    )
    if raw_tensor_target is raw_stage_target:
        stage_target = resources.enter_context(raw_stage_target)
        return stage_target, stage_target

    tensor_target = resources.enter_context(raw_tensor_target)
    stage_target = resources.enter_context(raw_stage_target)
    return stage_target, tensor_target


def compile_model_to_file(
    runtime: Any,
    target: Any,
    source_model: Any,
    artifact_path: Path,
    *,
    external_weights_path: Path | None = None,
) -> Any:
    """Compile a model for one target and reload the artifact through Runtime."""

    with target.model_compiler() as compiler:
        compiler.compile_to_file(
            source_model,
            str(artifact_path),
            (
                str(external_weights_path)
                if external_weights_path is not None
                else None
            ),
        )
    return runtime.load_model(str(artifact_path))


def validate_stage_placement(stage: Any, request: ExecutionRequest) -> Placement:
    """Check the built stage used the requested hardware class and provider."""

    with stage.execution_target as actual_target:
        actual_device = actual_target.kind.name.casefold()
    if actual_device != request.device:
        raise RuntimeError(
            f"Runtime selected {actual_device}, but --device requested {request.device}."
        )

    with stage.ort_diagnostics() as diagnostics:
        requested_provider = diagnostics.requested_provider
        selected_provider = diagnostics.selected_provider
        provider_pinned = diagnostics.is_provider_pinned

    if request.ep:
        # Only an --ep request asserts a provider. A default stage may report no
        # provider name.
        if not selected_provider:
            raise RuntimeError(
                "The stage did not report the execution provider requested by --ep."
            )
        expected_provider = canonical_execution_provider(request.ep)
        assert expected_provider is not None
        expected = expected_provider.casefold()
        if not provider_pinned:
            raise RuntimeError("The requested execution provider was not pinned.")
        if not requested_provider or requested_provider.casefold() != expected:
            raise RuntimeError(
                "The stage did not report the execution provider requested by --ep."
            )
        if selected_provider.casefold() != expected:
            raise RuntimeError(
                f"The stage selected {selected_provider}, but --ep requested "
                f"{expected_provider}."
            )

    return Placement(
        device=actual_device,
        selected_provider=selected_provider,
    )


def validate_and_print_stage_placement(
    stage: Any,
    request: ExecutionRequest,
    verbose: bool,
) -> Placement:
    """Validate placement and optionally print the sample's common diagnostic."""

    placement = validate_stage_placement(stage, request)
    if verbose:
        print(
            "      placement: "
            f"device={placement.device}, "
            f"provider={placement_provider_label(request, placement)}"
        )
    return placement


def placement_provider_label(
    request: ExecutionRequest,
    placement: Placement,
) -> str:
    """Describe a stage provider for diagnostics output.

    A default stage may report no provider name.
    """

    return placement.selected_provider or "default provider not reported"


def validate_tensor_desc(
    desc: tuple[Any, tuple[int, ...]],
    expected_type: Any,
    expected_shape: tuple[int, ...],
    label: str,
) -> None:
    """Check a Runtime tensor descriptor against one sample contract."""

    data_type, shape = desc
    if data_type != expected_type or shape != expected_shape:
        raise RuntimeError(
            f"{label} tensor shape mismatch: {data_type.name} {shape}"
        )


@contextmanager
def working_artifact_directory(label: str) -> Iterator[Path]:
    """Create and remove a temporary artifact directory for compiled outputs."""

    with tempfile.TemporaryDirectory(prefix=f"winml-python-{label}-") as path:
        yield Path(path)


def prepare_output_directory(path: Path) -> Path:
    """Create a caller-owned output directory, refusing non-empty destinations."""

    resolved = path.resolve()
    if resolved.exists():
        if not resolved.is_dir():
            raise ValueError(f"Output path is not a directory: {resolved}")
        if any(resolved.iterdir()):
            raise ValueError(f"Output directory must be empty: {resolved}")
    else:
        resolved.mkdir(parents=True)
    return resolved
