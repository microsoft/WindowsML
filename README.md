# Windows ML

[![Status: Generally Available](https://img.shields.io/badge/status-Generally%20Available-brightgreen)](https://blogs.windows.com/windowsdeveloper/2025/09/23/windows-ml-is-generally-available-empowering-developers-to-scale-local-ai-across-windows-devices/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Windows ML is the unified and high-performance local AI inferencing framework for Windows, powered by [ONNX Runtime](https://onnxruntime.ai/). With Windows ML, you can run AI models locally and accelerate inference on NPUs, GPUs, and CPUs through optional execution providers that Windows manages and keeps up to date. You can use models from PyTorch, TensorFlow/Keras, TFLite, scikit-learn, and convert them to ONNX to use them with Windows ML.

<img src="assets/windows-ml.png" alt="Windows ML architecture" width="720">

## Why Windows ML

Windows ML is Microsoft's recommended local AI inferencing framework for Windows — the official, Windows-native way to run custom and open-source AI models on Windows PCs, with hardware-accelerated inference across **CPU**, **GPU**, and **NPU**. It's built and optimized for **Scale**, **Performance**, and **Deployment** across the Windows ecosystem.

- **Run AI on-device** — models run locally on the user's hardware, keeping data private, reducing latency, eliminating cloud costs, and working without an internet connection.
- **Use models you already have** — bring models from PyTorch, TensorFlow, scikit-learn, Hugging Face, and more, convert them to ONNX, and use them with Windows ML.
- **Scale across silicon** - Windows ML is powered by ONNX Runtime and offers broad hardware support, so you can scale your workloads across Windows PCs with any hardware configuration.
- **Hardware acceleration, facilitated by Windows** — Windows ML allows you to access NPUs, GPUs, and CPUs via execution providers that Windows installs and keeps up to date — no need to bundle them in your app.
- **One runtime, many apps** — optionally use Windows ML as a shared system component, so your app stays small and all apps on the device share the same up-to-date runtime, rather than every app bundling its own copy.
- **Windows-supported** — regardless of how you deploy, you get Windows-maintained, optimized runtime dependencies built for stability across updates.
- **Best-in-class performance** — Windows ML delivers performance on par with dedicated SDKs like TensorRT for RTX or Qualcomm's AI Engine Direct. See [Accelerate AI models](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/accelerate-ai-models) for hardware and model-specific guidance.

To learn about the benefits of using Windows ML compared to ONNX Runtime directly, see the [Windows ML docs](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/overview#why-use-windows-ml-instead-of-microsoft-ort).

## NuGet packages

| Package | Use it for |
|---|---|
| [![NuGet: Microsoft.WindowsAppSDK.ML](https://img.shields.io/nuget/v/Microsoft.WindowsAppSDK.ML?label=Microsoft.WindowsAppSDK.ML)](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.ML) | Recommended way to use Windows ML (supports both framework-dependent and self-contained) |
| [![NuGet: Microsoft.Windows.AI.MachineLearning](https://img.shields.io/nuget/v/Microsoft.Windows.AI.MachineLearning?label=Microsoft.Windows.AI.MachineLearning)](https://www.nuget.org/packages/Microsoft.Windows.AI.MachineLearning) | Alternative way to use Windows ML (only self-contained, no Windows App SDK dependency) |
| [![NuGet: Microsoft.ML.OnnxRuntimeGenAI.WinML](https://img.shields.io/nuget/v/Microsoft.ML.OnnxRuntimeGenAI.WinML?label=Microsoft.ML.OnnxRuntimeGenAI.WinML)](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntimeGenAI.WinML) | Generative AI (Phi, Llama, Mistral, Gemma, DeepSeek…) on top of Windows ML |

## Get started

To get started with Windows ML, [see our documentation on Microsoft Learn](https://learn.microsoft.com/windows/ai/new-windows-ml/get-started).

## Samples

Windows ML code samples can be found [on our documentation on Microsoft Learn](https://learn.microsoft.com/windows/ai/new-windows-ml/samples).

## Filing issues & feedback

**Found a bug, have a question, or want to suggest a sample?** [Open an issue in this repo](../../issues) — we triage them directly. For broader Windows ML platform discussions or runtime/API issues that span beyond the samples, you can also use the [Windows App SDK repo](https://github.com/microsoft/WindowsAppSDK/issues).

## Learn more

- 📖 [What is Windows ML?](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/overview)
- 📣 [Windows ML is generally available (Windows Developer Blog, Sept 2025)](https://blogs.windows.com/windowsdeveloper/2025/09/23/windows-ml-is-generally-available-empowering-developers-to-scale-local-ai-across-windows-devices/)
- 🚀 [Accelerate AI models on NPU / GPU / CPU](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/accelerate-ai-models)
- 📦 [Distributing your app](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/distributing-your-app)
- 🛠️ [Convert models to ONNX](https://code.visualstudio.com/docs/intelligentapps/modelconversion)

## Contributing

This project welcomes contributions and suggestions. Most contributions require you to agree to a Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft trademarks or logos is subject to and must follow [Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general). Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship. Any use of third-party trademarks or logos are subject to those third-party's policies.

## License

See [LICENSE](LICENSE) for code and [LICENSE-DOCS](LICENSE-DOCS) for documentation.
