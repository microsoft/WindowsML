# Copyright (C) Microsoft Corporation. All rights reserved.

"""Shared console failure handling for Python Runtime sample entry points.

This module is plumbing only: it normalizes expected sample failures into one
diagnostic line and leaves Runtime API usage to the calling sample.
"""

from __future__ import annotations

import sys
from typing import Callable, NoReturn


def run_entry_point(main: Callable[[], int]) -> NoReturn:
    """Run one sample ``main`` and report expected failures as ERROR diagnostics.

    ``windowsml.runtime.WinMLError`` derives from :class:`OSError`, so a failing
    Runtime call - including ``NotSupportedError`` and a missing optional
    backend payload - is reported the same way as a missing file or an invalid
    argument: one ``ERROR:`` line on stderr and exit code 2, never a traceback.
    """

    # Model replies can contain characters outside the console code page.
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="replace")

    try:
        raise SystemExit(main())
    except (ValueError, RuntimeError, OSError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(2) from None
