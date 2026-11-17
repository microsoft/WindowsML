// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. The tensor names of an ONNX model. See
// ..\README.md.

using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;

namespace WinMLSamples.Runtime;

// The tensor names of an ONNX model. Query a Model loaded from a .onnx file for
// it; compiled .ort artifacts keep ordinals but not names.
public sealed unsafe class OrtModelSchema : WinMLObject, IWinMLInterface<OrtModelSchema>
{
    private OrtModelSchema(IWinMLOrtModelSchema* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLOrtModelSchema.IID_Guid;

    static OrtModelSchema IWinMLInterface<OrtModelSchema>.CreateFromOwnedPointer(nint pointer) => new((IWinMLOrtModelSchema*)pointer);

    private IWinMLOrtModelSchema* Self => (IWinMLOrtModelSchema*)ThisPtr;

    // The ordinal of the input named name.
    public uint FindInputIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        uint index;
        fixed (char* namePointer = name)
        {
            Check(Self->FindInputIndex(new PCWSTR(namePointer), &index));
        }

        return index;
    }

    // The ordinal of the output named name.
    public uint FindOutputIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        uint index;
        fixed (char* namePointer = name)
        {
            Check(Self->FindOutputIndex(new PCWSTR(namePointer), &index));
        }

        return index;
    }
}
