# Windows ML execution provider releases

Windows ML includes dynamically-acquired execution providers via the Windows ML `ExecutionProviderCatalog` APIs. To learn more about how to use these, see [Install Windows ML EPs](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers) and [Register Windows ML EPs](https://learn.microsoft.com/windows/ai/new-windows-ml/register-execution-providers). Updated versions of the execution providers are made available via [Windows Update's optional nonsecurity preview releases, a.k.a. "D week releases"](https://learn.microsoft.com/windows/deployment/update/release-cycle#optional-nonsecurity-preview-release).

## Windows ML 2.x

The following execution providers are available to developers using [Microsoft.WindowsAppSDK.ML](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.ML) or [Microsoft.Windows.AI.MachineLearning](https://www.nuget.org/packages/Microsoft.Windows.AI.MachineLearning) version `2.x`:

> Only the current version listed for each execution provider is supported. Upcoming versions are in preview and not guaranteed to be released. Past versions are shown for release history only.

| Execution provider | Current version and release date | Upcoming version and planned release dates |
| --- | --- | --- |
| [MIGraphX (AMD)](#migraphx-amd) | MSIX: `1.8.57.0`<br>GPU EP: `7.2.2606.20`<br>Released: `2026 6D` | MSIX: `1.8.60.0`<br>GPU EP: `7.2.2608.30`<br>Insiders: `2026 7C`<br>GA: `2026 8D` |
| [NvTensorRtRtx (NVIDIA)](#nvtensorrtrtx-nvidia) | MSIX: `2.30.43.0`<br>Released: `2026 7D` |  |
| [OpenVINO (Intel)](#openvino-intel) | MSIX: `1.8.80.0`<br>OpenVINO: `1.4.1`<br>Released: `2026 6D` | MSIX: `1.8.84.0`<br>OpenVINO: `1.6.1`<br>Insiders: `2026 7C`<br>GA: `2026 8D` |
| [QNN (Qualcomm)](#qnn-qualcomm) | MSIX: `2.2451.48.0`<br>QAIRT: `2.45.41`<br>Released: `2026 7D` | MSIX: `2.2480.49.0`<br>QAIRT: `2.48.40`<br>Insiders: `2026 7C`<br>GA: `2026 8D` |
| [VitisAI (AMD)](#vitisai-amd) | MSIX: `1.8.68.0`<br>EP: 6059<br>Released: `2026 7D` | MSIX: `1.8.72.0`<br>EP: 1276<br>Insiders: `2026 7C`<br>GA: `2026 8D` |
| [WebGPU (Microsoft)](#webgpu-experimental) | MSIX: `0.2.1.0` |  |

## Windows ML 1.8.x

The following execution providers are available to developers using [Microsoft.WindowsAppSDK.ML](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.ML) version `1.8.x`:

> Only the current version listed for each execution provider is supported. Upcoming versions are in preview and not guaranteed to be released. Past versions are shown for release history only.

| Execution provider | Current version and release date | Upcoming version and planned release dates | Required [Microsoft.WindowsAppSDK.ML](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.ML) `1.8.x` |
| --- | --- | --- | --- |
| [MIGraphX (AMD)](#migraphx-amd) | MSIX: `1.8.57.0`<br>GPU EP: `7.2.2606.20`<br>Released: `2026 6D` | MSIX: `1.8.60.0`<br>GPU EP: `7.2.2608.30`<br>Insiders: `2026 7C`<br>GA: `2026 8D` | `1.8.2109` or greater |
| [NvTensorRtRtx (NVIDIA)](#nvtensorrtrtx-nvidia) | MSIX: `1.8.24.0`<br>Released: `2026 2D` |  | Any `1.8.x` version |
| [OpenVINO (Intel)](#openvino-intel) | MSIX: `1.8.80.0`<br>OpenVINO: `1.4.1`<br>Released: `2026 6D` | MSIX: `1.8.84.0`<br>OpenVINO: `1.6.1`<br>Insiders: `2026 7C`<br>GA: `2026 8D` | Any `1.8.x` version |
| [QNN (Qualcomm)](#qnn-qualcomm) | MSIX: `1.8.30.0`<br>QAIRT: `2.40.0.251030`<br>Released: `2026 1D` |  | Any `1.8.x` version |
| [VitisAI (AMD)](#vitisai-amd) | MSIX: `1.8.68.0`<br>EP: 6059<br>Released: `2026 7D` | MSIX: `1.8.72.0`<br>EP: 1276<br>Insiders: `2026 7C`<br>GA: `2026 8D` | Any `1.8.x` version |

> Release dates are in the format of "2025 11D", or "[YEAR] [MONTH][WEEK]". "2025 11D" means it was released on the "D" week (4th week) of November 2025.
> Previews are released to Windows Insiders and are often released on the "A" week (1st week) of the month. Users must be in the Windows Insider program to get those versions.

## Release history

### MIGraphX (AMD)

| Version | Compatible with | Windows Update release | Release notes |
| --- | --- | --- | --- |
| 1.8.57.0 | Windows ML 2.x and 1.8.x | 2026 6D | |
| 1.8.56.0 | Windows ML 2.x and 1.8.x | 2026 5D | |
| 1.8.55.0 | Windows ML 2.x and 1.8.x | 2026 4D | |
| 1.8.51.0 | Windows ML 2.x and 1.8.x | 2026 3D | |
| 1.8.43.0 | Windows ML 2.x and 1.8.x | 2026 1D | |
| 1.8.35.0 | Windows ML 2.x and 1.8.x | 2025 11D | |

### NvTensorRtRtx (NVIDIA)

| Version | Compatible with | Windows Update release | Release notes |
| --- | --- | --- | --- |
| 2.30.43.0 | Windows ML 2.x | 2026 7D | |
| 0.0.40.0 | Windows ML 2.x | 2026 6D | |
| 0.0.33.0 | Windows ML 2.x | 2026 5D | |
| 0.0.28.0 | Windows ML 2.x | 2026 4D | |
| 0.0.26.0 | Windows ML 2.x | 2026 3D | |
| 1.8.24.0 | Windows ML 1.8.x | 2026 2D | Bug fix for WebNN. |
| 1.8.22.0 | Windows ML 1.8.x | 2026 1D | |
| 1.8.14.0 | Windows ML 1.8.x | 2025 9D | |

### OpenVINO (Intel)

For release notes of each OpenVINO version, see the [OpenVINO NuGet package](https://www.nuget.org/packages/Intel.ML.OnnxRuntime.EP.OpenVINO) and OpenVINO's [2026.x](https://docs.openvino.ai/2026/about-openvino/release-notes-openvino.html) and [2025.x](https://docs.openvino.ai/2025/about-openvino/release-notes-openvino.html) release notes.

| Version | Compatible with | Windows Update release | OpenVINO version |
| --- | --- | --- | --- |
| 1.8.80.0 | Windows ML 2.x and 1.8.x | 2026 6D | OpenVINO 1.4.1 |
| 1.8.79.0 | Windows ML 2.x and 1.8.x | 2026 5D | OpenVINO 1.4.0 |
| 1.8.69.0 | Windows ML 2.x and 1.8.x | 2026 3D | OpenVINO 2026.0 |
| 1.8.63.0 | Windows ML 2.x and 1.8.x | 2026 1D | OpenVINO 2025.4.1 |
| 1.8.26.0 | Windows ML 2.x and 1.8.x | 2025 11D | OpenVINO 2025.3 |
| 1.8.18.0 | Windows ML 2.x and 1.8.x | 2025 10D | OpenVINO 2025.3 |
| 1.8.15.0 | Windows ML 2.x and 1.8.x | 2025 9D | |

### QNN (Qualcomm)

For release notes of each QNN QAIRT SDK version, see [Qualcomm AI Runtime (QAIRT) SDK Release Notes](https://docs.qualcomm.com/doc/80-63442-10/topic/release_notes.html).

| Version | Compatible with | Windows Update release | QNN QAIRT SDK version |
| --- | --- | --- | --- |
| 2.2451.48.0 | Windows ML 2.x | 2026 7D | QNN 2.45.41 |
| 2.2450.47.0 | Windows ML 2.x | 2026 5D | QNN 2.45 |
| 2.2420.43.0 | Windows ML 2.x | 2026 4D | QNN 2.42 |
| 1.8.21.0 | Windows ML 1.8.x | 2025 11D | QNN 2.39 |
| 1.8.14.0 | Windows ML 1.8.x | 2025 10D | |
| 1.8.13.0 | Windows ML 1.8.x | 2025 9D | |

### VitisAI (AMD)

| Version | Compatible with | Windows Update release | Release notes |
| --- | --- | --- | --- |
| 1.8.68.0 | Windows ML 2.x and 1.8.x | 2026 7D | |
| 1.8.63.0 | Windows ML 2.x and 1.8.x | 2026 6D | |
| 1.8.62.0 | Windows ML 2.x and 1.8.x | 2026 5D | |
| 1.8.59.0 | Windows ML 2.x and 1.8.x | 2026 4D | |
| 1.8.55.0 | Windows ML 2.x and 1.8.x | 2026 3D | |
| 1.8.53.0 | Windows ML 2.x and 1.8.x | 2026 2D | Support for Procyon V2 Models with 13% improved score on GPT2 Machine in Turbo mode. Bug fix for disk space consumption on system C drive after restart. |
| 1.8.50.0 | Windows ML 2.x and 1.8.x | 2026 1D | |
| 1.8.43.0 | Windows ML 2.x and 1.8.x | 2025 11D (Windows Insiders) | |
| 1.8.31.0 | Windows ML 2.x and 1.8.x | 2025 11A (Windows Insiders) | |
| 1.8.26.0 | Windows ML 2.x and 1.8.x | 2025 10D | |
| 1.8.24.0 | Windows ML 2.x and 1.8.x | 2025 9D | |

### WebGPU (Microsoft)

| Version | Compatible with | Windows Update release | Release notes |
| --- | --- | --- | --- |
| 0.2.1.0 | Windows ML 2.x | | |
