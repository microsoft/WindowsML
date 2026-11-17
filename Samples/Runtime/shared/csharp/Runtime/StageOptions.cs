// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Stage settings that apply before Build:
// fixed extents for a model's symbolic ONNX dimensions, and ONNX Runtime
// session configuration entries. Query a Stage for them.

using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;

namespace WinMLSamples.Runtime;

// Resolves symbolic dimensions of an ONNX model stage, such as a Whisper
// encoder's feature_size, to fixed extents.
public sealed unsafe class OnnxSymbolicDimensionOverrides : WinMLObject, IWinMLInterface<OnnxSymbolicDimensionOverrides>
{
    private OnnxSymbolicDimensionOverrides(IWinMLOnnxSymbolicDimensionOverrides* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLOnnxSymbolicDimensionOverrides.IID_Guid;

    static OnnxSymbolicDimensionOverrides IWinMLInterface<OnnxSymbolicDimensionOverrides>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLOnnxSymbolicDimensionOverrides*)pointer);

    // Gives the symbolic dimension name the extent extent.
    public void SetOverride(string name, long extent)
    {
        ArgumentNullException.ThrowIfNull(name);
        fixed (char* dimension = name)
        {
            Check(((IWinMLOnnxSymbolicDimensionOverrides*)ThisPtr)->SetOverride(new PCWSTR(dimension), extent));
        }
    }
}

// ONNX Runtime options of a stage that ONNX Runtime runs.
public sealed unsafe class OrtStageOptions : WinMLObject, IWinMLInterface<OrtStageOptions>
{
    private OrtStageOptions(IWinMLOrtStageOptions* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLOrtStageOptions.IID_Guid;

    static OrtStageOptions IWinMLInterface<OrtStageOptions>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLOrtStageOptions*)pointer);

    // Adds an ONNX Runtime session configuration entry, such as
    // "optimization.disable_specified_optimizers", to the stage's session.
    public void SetSessionConfigEntry(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        fixed (char* entryKey = key)
        fixed (char* entryValue = value)
        {
            Check(((IWinMLOrtStageOptions*)ThisPtr)->SetSessionConfigEntry(new PCWSTR(entryKey), new PCWSTR(entryValue)));
        }
    }
}
