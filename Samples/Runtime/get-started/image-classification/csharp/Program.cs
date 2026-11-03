// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Image classification with a one-stage Windows ML Runtime pipeline, from C#.
//
// Classify one image with SqueezeNet and print the top five ImageNet labels.
// This is the C# counterpart of ..\main.cpp and prints the same predictions.
//
//   WIC decodes the image to BGRA pixels, and the CPU target's
//   IWinMLImageTensorFactory resizes, normalizes, and repacks them into a
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
// The Runtime API is a COM-style C API. CsWin32 generates the C# bindings when
// the project builds, from the Win32 metadata in the
// Microsoft.Windows.AI.MachineLearning package; NativeMethods.txt here and in
// ..\..\..\shared\csharp lists the APIs to generate. Generated interface
// methods throw an exception for a failure HRESULT and return their
// [out, retval] value.

using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using WinMLSamples;
using Win32 = Windows.Win32.PInvoke;

namespace ImageClassification;

internal static unsafe class Program
{
    private const uint ImageSize = 224;
    private const int PredictionCount = 5;

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
            // Failure HRESULTs surface as exceptions whose message includes the HRESULT.
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (ex.InnerException is not null)
            {
                Console.Error.WriteLine($"       {ex.InnerException.Message}");
            }

            return 1;
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

        using ComObjects objects = new();

        // The Runtime is the factory for everything else. Factory functions
        // return an HRESULT; interface methods throw on failure.
        IWinMLRuntime* runtime;
        Guid runtimeIid = typeof(IWinMLRuntime).GUID;
        PInvoke.WinMLCreateRuntime(&runtimeIid, (void**)&runtime).ThrowOnFailure();
        objects.Add(runtime);

        // The image tensor is created on a CPU target.
        IWinMLExecutionTarget* imageTarget = objects.Add(runtime->CreateCpuExecutionTarget());
        Console.WriteLine("[1/5] Runtime and CPU image-tensor target created.");

        IWinMLTensor* input = objects.Add(CreateImageTensor(imageTarget, options.ImagePath, objects));
        WINML_TENSOR_DESC inputDesc = input->GetDesc();
        if (FormatShape(inputDesc) != $"[1,3,{ImageSize},{ImageSize}]")
        {
            throw new InvalidDataException($"Unexpected image tensor shape {FormatShape(inputDesc)}.");
        }

        Console.WriteLine($"[2/5] Image decoded, resized, and normalized to {FormatShape(inputDesc)}.");

        // The model stage gets its own target from --device and --ep. A
        // requested provider is registered first, then the target is created.
        IWinMLExecutionTarget* stageTarget = ExecutionTargets.PrepareAndCreate(runtime, device, objects);
        IWinMLModel* model = objects.Add(runtime->LoadModelFromFile(options.ModelPath, null));
        Console.WriteLine($"[3/5] Model ready for {DeviceOptions.KindName(device.Kind)}.");

        // A stage places one model on one execution target. The Runtime
        // publishes only outputs requested before Build, and Build validates
        // that the model can run on the target.
        IWinMLPipelineBuilder* builder = objects.Add(runtime->CreatePipelineBuilder());
        IWinMLStage* stage = objects.Add(builder->AddModelStage(model, stageTarget, "squeezenet"));
        stage->RequestOutput(0);
        if (device.Verbose)
        {
            StageDiagnostics.Enable(stage, objects);
        }

        IWinMLPipeline* pipeline = objects.Add(builder->Build());
        IWinMLExecutionTarget* resolvedTarget = objects.Add(stage->GetExecutionTarget());
        Console.WriteLine($"[4/5] One-stage pipeline built. Resolved target: {DeviceOptions.KindName(resolvedTarget->GetKind())}");
        if (device.Verbose)
        {
            StageDiagnostics.Print(stage, objects);
        }

        // Bindings are positional: input ordinal 0, not an ONNX tensor name.
        // The CPU input tensor can feed a stage on any target.
        stage->BindInput(0, input);
        pipeline->Run();
        IWinMLTensor* output = objects.Add(stage->GetOutput(0));
        float[] probabilities = Softmax(ReadFloats(output));
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

    private static IWinMLTensor* CreateImageTensor(IWinMLExecutionTarget* target, string imagePath, ComObjects objects)
    {
        // Execution targets expose the image tensor factory through QueryInterface.
        IWinMLImageTensorFactory* factory = objects.QueryInterface<IWinMLImageTensorFactory>(target);
        DecodedImage image = ImageFile.DecodeBgra8(imagePath);
        WINML_IMAGE_TENSOR_DESC layout = CreateImageNetLayout();

        // The factory resizes, reorders channels, normalizes, and repacks the
        // pixels into NCHW order in one call.
        fixed (byte* pixels = image.Pixels)
        {
            return factory->CreateTensorFromImageMemory(
                pixels,
                (ulong)image.Pixels.Length,
                image.Width,
                image.Height,
                image.Width * 4,
                (uint)DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                &layout);
        }
    }

    // An ImageNet classifier wants NCHW float32, RGB, with standard
    // per-channel normalization: the values WinMLTensorsDefaultImageLayout()
    // gives the C++ sample.
    private static WINML_IMAGE_TENSOR_DESC CreateImageNetLayout()
    {
        WINML_IMAGE_TENSOR_DESC layout = new()
        {
            dataType = WINML_TENSOR_DATA_TYPE.WINML_TENSOR_DATA_TYPE_FLOAT32,
            layout = WINML_TENSOR_LAYOUT_FORMAT.WINML_TENSOR_LAYOUT_FORMAT_NCHW,
            channelOrder = WINML_TENSOR_CHANNEL_ORDER.WINML_TENSOR_CHANNEL_ORDER_RGB,
            batchSize = 1,
            channels = 3,
            width = ImageSize,
            height = ImageSize,
            resizeMode = WINML_IMAGE_RESIZE_MODE.WINML_IMAGE_RESIZE_MODE_STRETCH,
        };

        // ImageNet normalization in 0-255 pixel units: value = (pixel - mean) * scale.
        Span<float> mean = layout.normalization.mean.AsSpan();
        Span<float> scale = layout.normalization.scale.AsSpan();
        mean[0] = 123.675f;
        mean[1] = 116.28f;
        mean[2] = 103.53f;
        scale[0] = 1.0f / (0.229f * 255.0f);
        scale[1] = 1.0f / (0.224f * 255.0f);
        scale[2] = 1.0f / (0.225f * 255.0f);
        scale[3] = 1.0f;
        return layout;
    }

    private static float[] ReadFloats(IWinMLTensor* tensor)
    {
        WINML_TENSOR_DESC desc = tensor->GetDesc();
        if (desc.dataType != WINML_TENSOR_DATA_TYPE.WINML_TENSOR_DATA_TYPE_FLOAT32)
        {
            throw new InvalidDataException($"Expected a FLOAT32 output, not {desc.dataType}.");
        }

        ulong count = 1;
        for (uint i = 0; i < desc.dimensionCount; ++i)
        {
            count *= desc.dimensions[i];
        }

        // A synchronized lock makes the output readable on the CPU wherever it
        // was produced. Release the lock as soon as the values are copied.
        IWinMLTensorDataLock* dataLock = tensor->Lock(
            WINML_TENSOR_LOCK_MODE.WINML_TENSOR_LOCK_MODE_READ,
            WINML_TENSOR_LOCK_FLAGS.WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS);
        try
        {
            byte* data;
            ulong byteCount;
            dataLock->GetData(&data, &byteCount);
            if (byteCount < count * sizeof(float))
            {
                throw new InvalidDataException($"The output holds {byteCount} bytes, fewer than {count} values.");
            }

            return new ReadOnlySpan<float>(data, checked((int)count)).ToArray();
        }
        finally
        {
            dataLock->Release();
        }
    }

    private static float[] Softmax(float[] logits)
    {
        float max = logits.Max();
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

    private static string FormatShape(WINML_TENSOR_DESC desc)
    {
        IEnumerable<ulong> dimensions = Enumerable.Range(0, (int)desc.dimensionCount).Select(i => desc.dimensions[i]);
        return $"[{string.Join(",", dimensions)}]";
    }
}

internal sealed record DecodedImage(uint Width, uint Height, byte[] Pixels);

// Decodes the first frame of an image file into 8-bit BGRA pixels with WIC. It
// makes the same calls as the C++ sample's WIC tensor adapter, so both samples
// pass identical pixels to the image tensor factory. WIC requires COM on the
// calling thread; .NET initializes the main thread of a console app into the
// multithreaded apartment unless Main has [STAThread], and this sample decodes
// on that thread. The Runtime itself does not require COM initialization.
internal static unsafe class ImageFile
{
    public static DecodedImage DecodeBgra8(string path)
    {
        using ComObjects objects = new();

        IWICImagingFactory* factory;
        Win32.CoCreateInstance(Win32.CLSID_WICImagingFactory, null, CLSCTX.CLSCTX_INPROC_SERVER, out factory).ThrowOnFailure();
        objects.Add(factory);

        IWICBitmapDecoder* decoder = objects.Add(factory->CreateDecoderFromFilename(
            Path.GetFullPath(path),
            null,
            GENERIC_ACCESS_RIGHTS.GENERIC_READ,
            WICDecodeOptions.WICDecodeMetadataCacheOnDemand));

        IWICBitmapFrameDecode* frame;
        decoder->GetFrame(0, &frame);
        objects.Add(frame);

        IWICBitmapSource* source = (IWICBitmapSource*)frame;
        Guid bgra = Win32.GUID_WICPixelFormat32bppBGRA;
        Guid format;
        source->GetPixelFormat(&format);
        if (format != bgra)
        {
            IWICFormatConverter* converter;
            factory->CreateFormatConverter(&converter);
            objects.Add(converter);
            converter->Initialize(
                source,
                &bgra,
                WICBitmapDitherType.WICBitmapDitherTypeNone,
                null,
                0.0,
                WICBitmapPaletteType.WICBitmapPaletteTypeMedianCut);
            source = (IWICBitmapSource*)converter;
        }

        uint width;
        uint height;
        source->GetSize(&width, &height);
        uint stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        fixed (byte* buffer = pixels)
        {
            source->CopyPixels(null, stride, (uint)pixels.Length, buffer);
        }

        return new DecodedImage(width, height, pixels);
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
