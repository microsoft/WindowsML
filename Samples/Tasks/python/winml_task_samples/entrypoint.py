# Copyright (C) Microsoft Corporation. All rights reserved.

"""Entry-point wrapper that reports common sample errors as CLI messages."""

from __future__ import annotations

from collections.abc import Callable
import sys


class FragmentPrinter:
    """Collect streamed fragments while printing them as the sample output."""

    def __init__(self, prefix: str) -> None:
        self._fragments: list[str] = []
        print(prefix, end="", flush=True)

    def __call__(self, fragment: str) -> None:
        self._fragments.append(fragment)
        print(fragment, end="", flush=True)

    @property
    def text(self) -> str:
        """Return the concatenated stream fragments."""

        return "".join(self._fragments)

    def require_matches(self, completed_text: str, message: str) -> None:
        """Verify the pull stream and terminal Task result agree."""

        if self.text != completed_text:
            raise RuntimeError(message)


def run_entry_point(main: Callable[[], int]) -> None:
    """Run a sample main function and turn expected failures into exit code 1."""

    # Model replies can contain characters outside the console code page.
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="replace")

    try:
        raise SystemExit(main())
    except (FileNotFoundError, RuntimeError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1) from error
