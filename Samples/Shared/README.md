# Shared

Common helper code reused across multiple samples. Keeps the per-sample `Program.cs` / `main.cpp` files small and focused on the Windows ML API surface being demonstrated.

## Contents

- **`cs/`** — C# helpers (`ModelManager`, `ExecutionProviderManager`, `ImageProcessor`, `InferenceEngine`, `ResultProcessor`, `ArgumentParser`) used by samples under `Samples/cs/`, `Samples/cs-wpf/`, `Samples/cs-winforms/`, and `Samples/cs-winui/`. Namespace: `WindowsML.Shared`.
- **`cpp/`** — C++ helpers used by samples under `Samples/cpp/` and `Samples/cpp-cmake/`.
- **`openvino/`** — OpenVINO execution-provider configuration shared by samples that opt in to the Intel OpenVINO EP.

> These helpers are intended as **sample-quality reference code**, not a production library. Copy and adapt patterns into your own app as needed.
