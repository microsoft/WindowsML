# Copyright (C) Microsoft Corporation. All rights reserved.

"""Verify Python wheel compatibility for the Task samples."""

from __future__ import annotations

import argparse
import hashlib
from importlib import metadata
import json
from pathlib import Path
import re


_LLAMA_DISTRIBUTIONS = ("windowsml-llama-core",)
# The CUDA backend is opt-in, so these are checked only when installed.
_OPTIONAL_LLAMA_DISTRIBUTIONS = ("windowsml-llama", "windowsml-llama-cuda")


def _distribution_record(name: str) -> dict[str, str]:
    distribution = metadata.distribution(name)
    metadata_file = next(
        (
            file
            for file in distribution.files or ()
            if file.name == "METADATA"
        ),
        None,
    )
    if metadata_file is None:
        raise RuntimeError(f"{name} does not expose installed METADATA")
    metadata_path = Path(distribution.locate_file(metadata_file))
    return {
        "name": distribution.metadata["Name"],
        "version": distribution.version,
        "location": str(distribution.locate_file("")),
        "metadataSha256": hashlib.sha256(metadata_path.read_bytes()).hexdigest(),
    }


def _pinned_version(distribution: str, requirement_name: str) -> str:
    """Returns the == version that distribution requires for requirement_name."""
    for requirement in metadata.distribution(distribution).requires or ():
        name = re.match(r"[A-Za-z0-9._-]+", requirement)
        if name is None or re.sub(r"[-_.]+", "-", name.group(0)).casefold() != requirement_name:
            continue
        pinned = re.search(r"==\s*([^,;\s)]+)", requirement)
        if pinned is None:
            raise RuntimeError(
                f"{distribution} must require {requirement_name} with ==; found "
                f"{requirement!r}"
            )
        return pinned.group(1)
    raise RuntimeError(f"{distribution} does not declare a {requirement_name} dependency")


def _release(version: str) -> tuple[int, ...]:
    """Returns the numeric release segments of a version such as 13.7.0.27."""
    release = re.match(r"\d+(?:\.\d+)*", version)
    if release is None:
        raise RuntimeError(f"cannot read the release number of version {version!r}")
    return tuple(int(part) for part in release.group(0).split("."))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--expected-windowsml-version", required=True)
    parser.add_argument(
        "--require-llama",
        action="store_true",
        help="Fail unless the windowsml-llama-core wheel is installed.",
    )
    args = parser.parse_args()

    windowsml = metadata.distribution("windowsml")
    if windowsml.version != args.expected_windowsml_version:
        raise RuntimeError(
            "windowsml version mismatch: "
            f"expected {args.expected_windowsml_version}, found {windowsml.version}"
        )

    ort_requirement = next(
        (
            requirement
            for requirement in windowsml.requires or ()
            if requirement.casefold().replace("_", "-").startswith(
                "onnxruntime-windowsml"
            )
        ),
        None,
    )
    if ort_requirement is None:
        raise RuntimeError(
            "windowsml does not declare an onnxruntime-windowsml dependency"
        )
    required_ort_version = re.search(r"==\s*([^,;\s)]+)", ort_requirement)
    if required_ort_version is None:
        raise RuntimeError(
            "windowsml must require onnxruntime-windowsml with ==; found "
            f"{ort_requirement!r}"
        )

    ort = metadata.distribution("onnxruntime-windowsml")
    if ort.version != required_ort_version.group(1):
        raise RuntimeError(
            "onnxruntime-windowsml version mismatch: "
            f"windowsml requires {required_ort_version.group(1)}, found {ort.version}"
        )

    records = {
        "windowsml": _distribution_record("windowsml"),
        "onnxruntime-windowsml": _distribution_record("onnxruntime-windowsml"),
    }

    # The llama.cpp wheels install into windowsml\lib and must come from the
    # same build as windowsml.
    for name in _LLAMA_DISTRIBUTIONS + _OPTIONAL_LLAMA_DISTRIBUTIONS:
        try:
            installed = metadata.distribution(name)
        except metadata.PackageNotFoundError:
            if args.require_llama and name in _LLAMA_DISTRIBUTIONS:
                raise RuntimeError(
                    f"{name} is not installed; install "
                    f"{name}=={windowsml.version}"
                ) from None
            continue
        if installed.version != windowsml.version:
            raise RuntimeError(
                f"{name} version mismatch: windowsml is {windowsml.version}, "
                f"found {installed.version}"
            )
        records[name] = _distribution_record(name)

    if "windowsml-llama-cuda" in records:
        # The CUDA backend runs beside the core llama.cpp payload.
        if "windowsml-llama-core" not in records:
            raise RuntimeError(
                "windowsml-llama-cuda is installed without windowsml-llama-core; "
                f"install windowsml-llama-core=={windowsml.version}"
            )
        try:
            cublas = metadata.distribution("nvidia-cublas")
        except metadata.PackageNotFoundError:
            raise RuntimeError(
                "windowsml-llama-cuda is installed without nvidia-cublas; install "
                f"windowsml-llama-cuda=={windowsml.version}"
            ) from None
        # Windows ML also loads a later cuBLAS with the same major version, so
        # only an earlier release or another major version fails.
        pinned_cublas = _pinned_version("windowsml-llama-cuda", "nvidia-cublas")
        required = _release(pinned_cublas)[:2]
        found = _release(cublas.version)[:2]
        if found[0] != required[0] or found < required:
            raise RuntimeError(
                f"nvidia-cublas {cublas.version} doesn't work with windowsml-llama-cuda, "
                f"which needs {required[0]}.{required[1]} or a later {required[0]}.x "
                f"release; install nvidia-cublas=={pinned_cublas}"
            )
        records["nvidia-cublas"] = _distribution_record("nvidia-cublas")

    print(json.dumps(records, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
