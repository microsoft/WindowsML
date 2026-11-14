// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Image classification with a one-stage Windows ML Runtime pipeline, from C#.
//
// Classify one image with SqueezeNet and print the top five ImageNet labels.
// This is the C# counterpart of ..\main.cpp and prints the same predictions.
//
//   Windows.Graphics.Imaging decodes the image to BGRA pixels, and the CPU
//   target's ImageTensorFactory resizes, normalizes, and repacks them into a
//   224x224 planar RGB tensor in one call. The model stage gets its own target
//   from --device and --ep, so input and model placement are chosen
//   separately. LoadModelFromFile, AddModelStage, RequestOutput(0), and Build
//   set up the stage, and Build fails if the model cannot run on that target.
//   BindInput(0) binds by position, Run executes, and GetOutput(0) is read
//   through a synchronized CPU lock before softmax picks the top five.
//
// Run it
//   .\run_image_classification.ps1 -Language csharp [-Device cpu|gpu|npu]
//       [-Ep <name>] [-Performance|-Efficiency] [-Diagnostics]
//
// The sample uses the sample wrapper in ..\..\..\shared\csharp\Runtime, an
// idiomatic C# layer over the Runtime API in the
// Microsoft.Windows.AI.MachineLearning package. Runtime objects are
// IDisposable, failures throw .NET exceptions that keep the HRESULT, and
// tensor data is read through a Span over the locked tensor memory. The
// wrapper is sample code; see ..\..\..\shared\csharp\README.md.

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinMLSamples;
using WinMLSamples.Runtime;

namespace ImageClassification;

internal static class Program
{
    private const uint ImageSize = 224;
    private const int PredictionCount = 5;

    // DXGI_FORMAT_B8G8R8A8_UNORM, the layout of the decoded pixels.
    private const uint Bgra8PixelFormat = 87;

    private static int Main(string[] args)
    {
        Options? options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Options.PrintUsage();
            return 2;
        }

        if (options is null)
        {
            Options.PrintUsage();
            return 0;
        }

        Console.WriteLine("=== Windows ML Runtime from C#: Image classification ===");
        Console.WriteLine();

        if (!options.AssetsExist())
        {
            Console.Error.WriteLine("ERROR: the model, image, or labels file was not found.");
            Console.Error.WriteLine("Run .\\check_artifacts.ps1 -Sample image-classification to see how to obtain them.");
            return 1;
        }

        try
        {
            Classify(options);
            return 0;
        }
        catch (Exception ex)
        {
            // Runtime failures keep their HRESULT in Exception.HResult.
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (ex.InnerException is not null)
            {
                Console.Error.WriteLine($"       {ex.InnerException.Message}");
            }

            // Like the C++ sample, exit with the failure HRESULT.
            return ex.HResult < 0 ? ex.HResult : 1;
        }
    }

    private static void Classify(Options options)
    {
        DeviceOptions device = options.Device;
        if (device.Verbose)
        {
            // Must run before the Runtime creates the ONNX Runtime environment.
            ExecutionProviders.EnableVerboseLogging();
        }

        // The Runtime is the factory for everything else.
        using WinMLRuntime runtime = WinMLRuntime.Create();

        // The image tensor is created on a CPU target.
        using ExecutionTarget imageTarget = runtime.CreateCpuExecutionTarget();
        Console.WriteLine("[1/5] Runtime and CPU image-tensor target created.");

        // The factory resizes, reorders channels, normalizes, and repacks the
        // pixels into NCHW order in one call. The sample waits for the decode,
        // so every Runtime call stays on this thread, as in the C++ sample.
        DecodedImage image = ImageFile.DecodeBgra8Async(options.ImagePath).GetAwaiter().GetResult();
        using Tensor input = imageTarget.ImageTensorFactory.CreateTensorFromImageMemory(
            image.Pixels,
            image.Width,
            image.Height,
            image.Width * 4,
            Bgra8PixelFormat,
            CreateImageNetLayout());
        string inputShape = FormatShape(input.Shape);
        if (inputShape != $"[1,3,{ImageSize},{ImageSize}]")
        {
            throw new InvalidDataException($"Unexpected image tensor shape {inputShape}.");
        }

        Console.WriteLine($"[2/5] Image decoded, resized, and normalized to {inputShape}.");

        // The model stage gets its own target from --device and --ep. A
        // requested provider is registered first, then the target is created.
        using ExecutionTarget stageTarget = ExecutionTargets.PrepareAndCreate(runtime, device);
        using Model model = runtime.LoadModelFromFile(options.ModelPath);
        Console.WriteLine($"[3/5] Model ready for {DeviceOptions.KindName(device.Kind)}.");

        // A stage places one model on one execution target. The Runtime
        // publishes only outputs requested before Build, and Build validates
        // that the model can run on the target.
        using PipelineBuilder builder = runtime.CreatePipelineBuilder();
        using Stage stage = builder.AddModelStage(model, stageTarget, "squeezenet");
        stage.RequestOutput(0);
        if (device.Verbose)
        {
            StageDiagnostics.Enable(stage);
        }

        using Pipeline pipeline = builder.Build();
        using (ExecutionTarget resolvedTarget = stage.GetExecutionTarget())
        {
            Console.WriteLine($"[4/5] One-stage pipeline built. Resolved target: {DeviceOptions.KindName(resolvedTarget.Kind)}");
        }

        if (device.Verbose)
        {
            StageDiagnostics.Print(stage);
        }

        // Bindings are positional: input ordinal 0, not an ONNX tensor name.
        // The CPU input tensor can feed a stage on any target.
        stage.BindInput(0, input);
        pipeline.Run();
        float[] probabilities;
        using (Tensor output = stage.GetOutput(0))
        {
            probabilities = Softmax(output);
        }

        Console.WriteLine("[5/5] Inference complete.");
        Console.WriteLine();

        string[] labels = File.ReadAllLines(options.LabelsPath);
        Console.WriteLine($"Top-{PredictionCount} predictions:");
        int rank = 1;
        foreach (int index in TopIndices(probabilities, PredictionCount))
        {
            string label = index < labels.Length ? labels[index] : $"class {index}";
            Console.WriteLine($"  #{rank++}  {probabilities[index] * 100.0f,6:F2}%  {label}");
        }
    }

    // An ImageNet classifier wants NCHW float32, RGB, with standard
    // per-channel normalization: the values WinMLTensorsDefaultImageLayout()
    // gives the C++ sample.
    private static ImageTensorDescription CreateImageNetLayout()
    {
        // ImageNet normalization in 0-255 pixel units: value = (pixel - mean) * scale.
        TensorNormalization normalization = default;
        normalization.Mean[0] = 123.675f;
        normalization.Mean[1] = 116.28f;
        normalization.Mean[2] = 103.53f;
        normalization.Scale[0] = 1.0f / (0.229f * 255.0f);
        normalization.Scale[1] = 1.0f / (0.224f * 255.0f);
        normalization.Scale[2] = 1.0f / (0.225f * 255.0f);
        normalization.Scale[3] = 1.0f;

        return new ImageTensorDescription
        {
            DataType = TensorDataType.Float32,
            Layout = TensorLayoutFormat.Nchw,
            ChannelOrder = TensorChannelOrder.Rgb,
            BatchSize = 1,
            Channels = 3,
            Width = ImageSize,
            Height = ImageSize,
            ResizeMode = ImageResizeMode.Stretch,
            Normalization = normalization,
        };
    }

    // Softmax over the output logits, read through the lock's span: a
    // synchronized lock makes the output readable on the CPU wherever it was
    // produced. A CPU output is read in place; a device output is a staged
    // copy. The lock is released when the method returns.
    private static float[] Softmax(Tensor output)
    {
        if (output.DataType != TensorDataType.Float32)
        {
            throw new InvalidDataException($"Expected a Float32 output, not {output.DataType}.");
        }

        // A staged lock can expose more memory than the shape covers.
        long count = output.ElementCount;
        using TensorLock<float> data = output.Lock<float>(TensorLockMode.Read, TensorLockFlags.AllowSynchronizedCpuAccess);
        if (data.Length < count)
        {
            throw new InvalidDataException($"The output holds {data.Length * sizeof(float)} bytes, fewer than {count} values.");
        }

        ReadOnlySpan<float> logits = data.AsReadOnlySpan()[..(int)count];
        float max = float.NegativeInfinity;
        foreach (float logit in logits)
        {
            max = Math.Max(max, logit);
        }

        float[] values = new float[logits.Length];
        double sum = 0.0;
        for (int i = 0; i < logits.Length; ++i)
        {
            values[i] = MathF.Exp(logits[i] - max);
            sum += values[i];
        }

        for (int i = 0; i < values.Length; ++i)
        {
            values[i] = (float)(values[i] / sum);
        }

        return values;
    }

    // Equal scores are ordered by the lower class index, matching GetTopK in the
    // C++ sample. SqueezeNet's scores are whole numbers, so ties are common.
    private static IEnumerable<int> TopIndices(float[] values, int count)
    {
        return Enumerable.Range(0, values.Length).OrderByDescending(i => values[i]).ThenBy(i => i).Take(count);
    }

    private static string FormatShape(long[] shape)
    {
        return $"[{string.Join(",", shape)}]";
    }
}

internal sealed record DecodedImage(uint Width, uint Height, byte[] Pixels);

// Decodes the first frame of an image file into 8-bit BGRA pixels with
// Windows.Graphics.Imaging, which uses the same WIC codecs as the C++ sample's
// WIC tensor adapter. Orientation metadata and color profiles are ignored, as
// they are there, so both samples pass identical pixels to the image tensor
// factory.
internal static class ImageFile
{
    public static async Task<DecodedImage> DecodeBgra8Async(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            using IRandomAccessStream stream = file.AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            return new DecodedImage(decoder.PixelWidth, decoder.PixelHeight, pixels.DetachPixelData());
        }
        catch (Exception ex) when (ex.Message.Length == 0)
        {
            // Codec failures, such as an unsupported format, can arrive without
            // a message. Report the system description of their HRESULT.
            throw WinMLException.GetExceptionForHResult(ex.HResult);
        }
    }
}

internal sealed record Options(DeviceOptions Device, string ModelPath, string ImagePath, string LabelsPath)
{
    // Returns null when help is requested.
    public static Options? Parse(string[] args)
    {
        DeviceOptions device = new();
        string? model = null;
        string? image = null;
        string? labels = null;
        for (int i = 0; i < args.Length; ++i)
        {
            if (device.TryParse(args, ref i))
            {
                continue;
            }

            switch (args[i].ToLowerInvariant())
            {
                case "--model":
                    model = DeviceOptions.Value(args, ref i);
                    break;
                case "--image":
                    image = DeviceOptions.Value(args, ref i);
                    break;
                case "--labels":
                    labels = DeviceOptions.Value(args, ref i);
                    break;
                case "-h":
                case "--help":
                case "/?":
                    return null;
                default:
                    throw new ArgumentException($"Unrecognized argument: {args[i]}");
            }
        }

        return new Options(
            device,
            model ?? SampleAssets.FindModel(Path.Combine("squeezenet", "SqueezeNet.onnx")),
            image ?? SampleAssets.FindModel(Path.Combine("squeezenet", "sample-image.jpg")),
            labels ?? SampleAssets.FindModel(Path.Combine("squeezenet", "imagenet_classes.txt")));
    }

    public static void PrintUsage()
    {
        Console.WriteLine($"Usage: image-classification-csharp.exe {DeviceOptions.Usage}");
        Console.WriteLine("                                      [--model <path>] [--image <path>] [--labels <path>]");
        Console.WriteLine("  The image tensor is always created on the CPU; --device selects the inference device.");
        Console.WriteLine("  --ep <name> pins one installed catalog provider to the requested hardware class;");
        Console.WriteLine("  --device npu requires it.");
        Console.WriteLine("  --performance/--efficiency steer adapter selection when several adapters are present.");
        Console.WriteLine("  --verbose shows ONNX Runtime node placement and the stage's execution diagnostics.");
    }

    public bool AssetsExist()
    {
        return File.Exists(ModelPath) && File.Exists(ImagePath) && File.Exists(LabelsPath);
    }
}
