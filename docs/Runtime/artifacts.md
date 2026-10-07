<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Models and artifacts

The samples never download models during build or run. `check_artifacts.ps1`
reports missing files, prints acquisition commands, and verifies size and
SHA-256 values from `Samples\shared\SampleArtifacts.psd1`.

Run checks from `Samples\Runtime`:

```powershell
.\check_artifacts.ps1 -Sample image-classification
```

Review the publisher's terms before running a printed acquisition command. Run
the check again after acquisition so the catalog verifies every file.

## Sample groups

| Group | Use |
|---|---|
| `image-classification` | SqueezeNet model, labels, and sample image. |
| `super-resolution` | SESR model, sample image, and prepared Runtime model. |
| `whisper` | Whisper encoder, decoder, vocabulary, and prepared Runtime models. |
| `hello-language-model` | Exported unified ONNX language decoder. |
| `llm-chat` | Exported unified and split ONNX language artifacts. |
| `llm-chat-gguf` | Cataloged GGUF language model. |
| `speech-to-language-model` | Whisper artifacts plus exported ONNX language artifacts. |
| `model-compilation` | Image-classification artifacts reused by the compiler sample. |

Use `Get-Help .\check_artifacts.ps1 -Full` to see every accepted group.

## Artifact flow

```text
source model or cataloged file
    |
    +-- check_artifacts.ps1 verifies cataloged files
    |
    +-- sample preparation scripts create Runtime-ready ONNX
    |
    +-- language exporter creates unified and split ONNX layouts
    |
    +-- IWinMLModelCompiler creates backend-native artifact + resources
                                                |
                                                +--> same Runtime pipeline API
```

`LoadModelFromFile` loads `.onnx`, `.ort`, and `.gguf` files. The file type
selects the backend; the sample target selects CPU, GPU, or NPU. See
[Backends and model formats](backends.md) for the unified and split language
layouts.

## Terms and package version

These models are published by third parties, not by Microsoft. The source link
is the publisher's page for each item. Review the terms published there and
determine whether your use is permitted before downloading or using a model.

The sample package version is pinned in
[`Samples\Directory.Packages.props`](../../Samples/Directory.Packages.props).

## Artifact catalog

The table below is generated from `Samples\shared\SampleArtifacts.psd1`, the
same catalog `check_artifacts.ps1` verifies against. Regenerate it with
`Samples\shared\Export-ArtifactTable.ps1` after changing the catalog.
<!-- BEGIN GENERATED ASSET TABLE -->
| Asset | Purpose | Source page | Size |
|---|---|---|---|
| ImageNet class labels | Maps classifier output indices to human-readable names. | [https://github.com/pytorch/hub](https://github.com/pytorch/hub) | 10 KB |
| Qwen2.5 0.5B Instruct (GGUF, Q4_K_M) | Supplies a quantized language model for the GGUF backend. | [https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF) | 468.6 MB |
| SESR x2 super resolution (ONNX, FP32) | Upscales an image by two times. | [https://huggingface.co/amd/ryzenai-sesr](https://huggingface.co/amd/ryzenai-sesr) | 92 KB |
| Sample photograph | Provides a default low-resolution input image. | [https://github.com/pytorch/hub](https://github.com/pytorch/hub) | 646 KB |
| SqueezeNet 1.1 image classifier (ONNX) | Classifies an image into ImageNet categories. | [https://github.com/microsoft/WindowsAppSDK-Samples](https://github.com/microsoft/WindowsAppSDK-Samples) | 1.2 MB |
| Sample photograph | Provides a default input image for classification. | [https://github.com/pytorch/hub](https://github.com/pytorch/hub) | 646 KB |
| Whisper medium decoder (ONNX, Q4F16) | Decodes encoded audio into transcript tokens. | [https://huggingface.co/onnx-community/whisper-medium-ONNX](https://huggingface.co/onnx-community/whisper-medium-ONNX) | 319.8 MB |
| Whisper medium encoder (ONNX, Q4F16) | Encodes audio features for speech recognition. | [https://huggingface.co/onnx-community/whisper-medium-ONNX](https://huggingface.co/onnx-community/whisper-medium-ONNX) | 172.3 MB |
| Whisper tokenizer | Supplies the Task API tokenizer definition. | [https://huggingface.co/onnx-community/whisper-medium-ONNX](https://huggingface.co/onnx-community/whisper-medium-ONNX) | 3.7 MB |
| Whisper tokenizer configuration | Supplies special tokens and decoding defaults. | [https://huggingface.co/onnx-community/whisper-medium-ONNX](https://huggingface.co/onnx-community/whisper-medium-ONNX) | 276 KB |
| Whisper vocabulary | Maps decoder tokens back to text. | [https://huggingface.co/onnx-community/whisper-medium-ONNX](https://huggingface.co/onnx-community/whisper-medium-ONNX) | 1,012 KB |

Artifacts produced on your machine rather than downloaded:

| Artifact | What it produces | Source page |
|---|---|---|
| Qwen2.5 0.5B Instruct exported to ONNX | Downloads Qwen2.5-0.5B-Instruct from Hugging Face and exports the Runtime and Task ONNX graphs. Creates a local Python environment and installs the required export packages. | [https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct) |
| SESR Runtime-ready ONNX | Removes identity nodes the Runtime pipeline does not need. | Derived from downloaded artifacts |
| Whisper Runtime-ready ONNX | Rewrites the downloaded Whisper encoder and decoder into the fixed shapes the Runtime pipeline binds. | Derived from downloaded artifacts |
<!-- END GENERATED ASSET TABLE -->
