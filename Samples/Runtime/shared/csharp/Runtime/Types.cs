// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. .NET names for the Runtime enumerations
// and descriptors that the samples use. Enumeration values equal the C API
// values, so the wrapper passes them through unchanged.

using System.Runtime.CompilerServices;

namespace WinMLSamples.Runtime;

public enum ExecutionTargetKind
{
    Cpu = 1,
    Gpu = 2,
    Npu = 3,
}

public enum ExecutionTargetPreference
{
    Default = 0,
    Performance = 1,
    Efficiency = 2,
}

public enum TensorDataType
{
    Undefined = 0,
    Float32 = 1,
    Float16 = 2,
    BFloat16 = 3,
    Int8 = 4,
    UInt8 = 5,
    Int16 = 6,
    UInt16 = 7,
    Int32 = 8,
    UInt32 = 9,
    Int64 = 10,
    UInt64 = 11,
    Float64 = 12,
    Bool = 13,
    Int4 = 14,
    UInt4 = 15,
}

public enum TensorLayoutFormat
{
    Unspecified = 0,
    Nchw = 1,
    Nhwc = 2,
    Nct = 16,
    Nt = 17,
    Nl = 32,
}

public enum TensorChannelOrder
{
    Unspecified = 0,
    Rgb = 1,
    Bgr = 2,
    Gray = 3,
    Rgba = 4,
    Bgra = 5,
}

public enum ImageResizeMode
{
    Stretch = 0,
    CenterCrop = 1,
    Letterbox = 2,
}

public enum TensorLockMode
{
    Read = 1,
    Write = 2,
    ReadWrite = 3,
}

[Flags]
public enum TensorLockFlags
{
    None = 0,

    // Allows a staged copy for a tensor that lives in device memory.
    AllowSynchronizedCpuAccess = 1,
}

public enum StagePlacementCoverage
{
    Unknown = 0,
    Full = 1,
    Partial = 2,
    None = 3,
}

public enum StageWorkUnit
{
    Unknown = 0,
    GraphNode = 1,
    CompiledSubgraph = 2,
    ModelLayer = 3,
    Stage = 4,
}

public enum StageWorkImplementation
{
    Unknown = 0,
    Cpu = 1,
    D3D12Driver = 2,
    D3D12Shader = 3,
    DirectML = 4,
    WebGpu = 5,
    VendorLibrary = 6,
}

[Flags]
public enum TokenizerEncodeFlags
{
    None = 0,
    AddSpecialTokens = 1,
}

public enum ConversationFormatterMode
{
    ModelDefault = 0,
    PortableJinjaV1 = 1,
    LegacyTemplateV1 = 2,
}

// A tensor's element type and dimensions.
public readonly record struct TensorDescription(TensorDataType DataType, ulong[] Dimensions);

// A model input or output as the model declares it. A dimension of
// ulong.MaxValue is free (dynamic).
public readonly record struct TensorSchemaDescription(TensorDataType DataType, ulong[] Dimensions);

// Per-channel values, indexed by channel.
[InlineArray(4)]
public struct TensorChannelValues
{
    private float _element0;
}

// Normalization applied after resizing: value = (pixel - Mean[c]) * Scale[c],
// in 0-255 pixel units.
public struct TensorNormalization
{
    public TensorChannelValues Scale;
    public TensorChannelValues Mean;
}

// How an image tensor factory lays out pixels as a tensor.
public struct ImageTensorDescription
{
    public TensorDataType DataType { get; set; }

    public TensorLayoutFormat Layout { get; set; }

    public TensorChannelOrder ChannelOrder { get; set; }

    public uint BatchSize { get; set; }

    public uint Channels { get; set; }

    public uint Width { get; set; }

    public uint Height { get; set; }

    public ImageResizeMode ResizeMode { get; set; }

    public TensorNormalization Normalization { get; set; }
}

// One conversation message. Role is a chat role such as "user".
public sealed class ConversationMessage
{
    public required string Role { get; init; }

    public required string Content { get; init; }
}

public sealed class ConversationRequest
{
    public ConversationFormatterMode FormatterMode { get; init; }

    public required IReadOnlyList<ConversationMessage> Messages { get; init; }

    // Ends the prompt with the template's assistant generation prompt.
    public bool AddGenerationPrompt { get; init; }
}
