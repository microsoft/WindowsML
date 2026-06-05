# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

import sys
import traceback

# This needs to be alive throughout the lifetime of the application
_instance = None

class WinML:
    def __new__(cls, *args, **kwargs):
        global _instance
        if _instance is None:
            _instance = super(WinML, cls).__new__(cls, *args, **kwargs)
            _instance._initialized = False
        return _instance

    def __init__(self):
        if self._initialized:
            return
        self._initialized = True

        from windowsml import EpCatalog
        self._catalog = EpCatalog()
        self._catalog.__enter__()
        self._providers = self._catalog.find_all_providers()
        self._ep_paths: dict[str, str] = {}
        for provider in self._providers:
            provider.ensure_ready()
            if not provider.library_path:
                continue
            self._ep_paths[provider.name] = provider.library_path
        self._registered_eps: list[str] = []

    def __del__(self):
        if hasattr(self, '_catalog'):
            try:
                self._catalog.__exit__(None, None, None)
            except Exception:
                pass

    def register_execution_providers_to_ort(self) -> list[str]:
        import onnxruntime as ort
        for name, path in self._ep_paths.items():
            if name not in self._registered_eps:
                try:
                    ort.register_execution_provider_library(name, path)
                    self._registered_eps.append(name)
                except Exception as e:
                    print(f"Failed to register execution provider {name}: {e}", file=sys.stderr)
                    traceback.print_exc()
        return self._registered_eps
