# Resources

Shared model assets, label files, and licenses used by samples in this repo.

Samples reference files in this folder via relative paths (or copy them at build time). Don't move or rename files here without updating the sample(s) that depend on them.

## Contents

- **`ResNet50/`** — ResNet-50 ONNX model used by the ResNet samples (`Samples/cpp/CppResnetBuildDemo`, `Samples/cs/ResnetBuildDemoCS`, `Samples/cpp-cmake/ResNet*`).
- **`SqueezeNet.onnx`** — SqueezeNet ONNX model used by `Samples/cpp/CppConsoleDesktop`, `Samples/cs/CSharpConsoleDesktop`, and `Samples/python/SqueezeNetPython`.
- **`SqueezeNet.Labels.txt`** / **`SqueezeNet.LICENSE.txt`** — ImageNet labels and the SqueezeNet model license.
- Other model and configuration files referenced by individual samples — see each sample's `README.md` for specifics.
