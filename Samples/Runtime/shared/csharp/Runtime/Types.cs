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

public enum ConversationToolChoice
{
    Auto = 0,
    None = 1,
    Required = 2,
    Named = 3,
}

public enum ConversationParallelToolCalls
{
    ModelDefault = 0,
    Disabled = 1,
    Enabled = 2,
}

public enum ConversationResponseFormat
{
    Text = 0,
    JsonObject = 1,
    JsonSchema = 2,
}

public enum ConversationEnableThinking
{
    ModelDefault = 0,
    Disabled = 1,
    Enabled = 2,
}

// One conversation message. Role is a chat role such as "user", "assistant",
// or "tool". An assistant message may carry the tool calls it made instead of
// content; a tool message carries one call's result as its content.
public sealed class ConversationMessage
{
    public required string Role { get; init; }

    public string? Content { get; init; }

    public string? ReasoningContent { get; init; }

    public IReadOnlyList<ConversationToolCall>? ToolCalls { get; init; }

    // The call a tool message answers.
    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }
}

// A tool call that an assistant message made. ArgumentsJson is a JSON object.
public sealed record ConversationToolCall(string Id, string Name, string ArgumentsJson);

// A tool that the model may call. ParametersJson is a JSON schema object.
public sealed class ConversationTool
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string ParametersJson { get; init; }
}

// Unset properties keep the native defaults: no tools, text output, and the
// model's own thinking behavior.
public sealed class ConversationRequest
{
    public ConversationFormatterMode FormatterMode { get; init; }

    public required IReadOnlyList<ConversationMessage> Messages { get; init; }

    public IReadOnlyList<ConversationTool>? Tools { get; init; }

    public ConversationToolChoice ToolChoice { get; init; }

    // The tool to call when ToolChoice is Named.
    public string? NamedToolChoice { get; init; }

    public ConversationParallelToolCalls ParallelToolCalls { get; init; }

    // Ends the prompt with the template's assistant generation prompt.
    public bool AddGenerationPrompt { get; init; }

    public ConversationResponseFormat ResponseFormat { get; init; }

    // A JSON schema for ResponseFormat JsonSchema, with an optional name and description.
    public string? ResponseSchemaJson { get; init; }

    public string? ResponseSchemaName { get; init; }

    public string? ResponseSchemaDescription { get; init; }

    public bool ResponseSchemaStrict { get; init; }

    public ConversationEnableThinking EnableThinking { get; init; }
}
