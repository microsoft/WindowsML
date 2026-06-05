---
page_type: sample
languages:
- python
products:
- windows
name: "Windows ML — Python"
urlFragment: WindowsMLPython
description: "Use Windows ML and ONNX Runtime from Python to run ONNX models across CPU, GPU, and NPU."
extendedZipContent:
- path: LICENSE
  target: LICENSE
---

# Windows ML — Python (SqueezeNet)

Run a SqueezeNet image-classification model with Windows ML execution providers from Python, using the [`windowsml`](https://pypi.org/project/windowsml/) PyPI package (a ctypes wrapper over the Windows ML flat C API) together with [`onnxruntime-windowsml`](https://pypi.org/project/onnxruntime-windowsml/).

## Steps

### 1. Download the test model

```powershell
..\Download-Model.ps1
```

### 2. Set up the Python environment

- Use Python 3.10 – 3.13.
- The Python installation should **not** be from the Microsoft Store (use python.org or `winget install Python.Python.3.12`).

### 3. Install dependencies

```powershell
.\Install-Requirements.ps1
```

This installs `pillow`, `numpy`, and `windowsml[with-ort]` (which brings in `onnxruntime-windowsml`).

### 4. Run the sample

```powershell
python main.py
```

The sample discovers the available Windows ML execution providers, registers them with ONNX Runtime, compiles the model for the selected hardware policy, and runs inference on every image in the `Images/` folder.
