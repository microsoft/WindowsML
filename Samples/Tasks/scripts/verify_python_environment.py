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

    # The llama.cpp wheel installs into windowsml\lib and must come from the same
    # build as windowsml.
    for name in _LLAMA_DISTRIBUTIONS:
        try:
            installed = metadata.distribution(name)
        except metadata.PackageNotFoundError:
            if args.require_llama:
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

    print(json.dumps(records, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
