// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. A media encoder turns an image tensor
// into the embedding rows a multimodal decoder takes, and a segment layout
// describes how many sequence positions those rows occupy. Text generation
// sessions create media encoders, and chat sessions use them.

using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;

namespace WinMLSamples.Runtime;

// The embeddings and layout that MediaEncoder.Encode produced. Dispose it to
// release both.
public sealed class MediaEncoding : IDisposable
{
    internal MediaEncoding(Tensor embeddings, SequenceSegmentLayout layout)
    {
        Embeddings = embeddings;
        Layout = layout;
    }

    public Tensor Embeddings { get; }

    public SequenceSegmentLayout Layout { get; }

    public void Dispose()
    {
        Layout.Dispose();
        Embeddings.Dispose();
    }
}

public sealed unsafe class MediaEncoder : WinMLObject, IWinMLInterface<MediaEncoder>
{
    internal MediaEncoder(IWinMLMediaEncoder* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLMediaEncoder.IID_Guid;

    static MediaEncoder IWinMLInterface<MediaEncoder>.CreateFromOwnedPointer(nint pointer) => new((IWinMLMediaEncoder*)pointer);

    internal IWinMLMediaEncoder* Raw => (IWinMLMediaEncoder*)ThisPtr;

    // The tensor that Encode takes, such as a preprocessed image.
    public TensorDescription MediaTensorDescription
    {
        get
        {
            WINML_TENSOR_DESC desc;
            Check(Raw->GetMediaTensorDesc(&desc));
            TensorDescription result = Tensor.ToDescription(desc);
            GC.KeepAlive(this);
            return result;
        }
    }

    // The width of each embedding row.
    public uint EmbeddingLength
    {
        get
        {
            uint length;
            Check(Raw->GetEmbeddingLength(&length));
            return length;
        }
    }

    public TensorDataType EmbeddingDataType
    {
        get
        {
            WINML_TENSOR_DATA_TYPE dataType;
            Check(Raw->GetEmbeddingDataType(&dataType));
            return (TensorDataType)dataType;
        }
    }

    // Encodes media into embedding rows. Pass embeddings to reuse a tensor
    // that an earlier call returned; the encoder may replace it.
    public MediaEncoding Encode(Tensor media, Tensor? embeddings = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        IWinMLTensor* output = null;
        if (embeddings is not null)
        {
            output = embeddings.Raw;
            ((Windows.Win32.System.Com.IUnknown*)output)->AddRef();
        }

        IWinMLSequenceSegmentLayout* layout = null;
        HRESULT hr = Raw->Encode(media.Raw, &output, &layout);
        GC.KeepAlive(this);
        GC.KeepAlive(media);
        GC.KeepAlive(embeddings);
        if (hr.Value < 0 || output == null || layout == null)
        {
            Release(output);
            Release(layout);
            WinMLException.ThrowIfFailed(hr);
            throw new InvalidOperationException("The Runtime returned no object.");
        }

        return new MediaEncoding(new Tensor(output), new SequenceSegmentLayout(layout));
    }
}

// How many embedding rows, positions, and sequence cells a segment occupies.
public sealed unsafe class SequenceSegmentLayout : WinMLObject, IWinMLInterface<SequenceSegmentLayout>
{
    internal SequenceSegmentLayout(IWinMLSequenceSegmentLayout* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLSequenceSegmentLayout.IID_Guid;

    static SequenceSegmentLayout IWinMLInterface<SequenceSegmentLayout>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLSequenceSegmentLayout*)pointer);

    internal IWinMLSequenceSegmentLayout* Raw => (IWinMLSequenceSegmentLayout*)ThisPtr;

    public uint EmbeddingRowCount
    {
        get
        {
            uint count;
            Check(Raw->GetEmbeddingRowCount(&count));
            return count;
        }
    }

    public uint PositionCount
    {
        get
        {
            uint count;
            Check(Raw->GetPositionCount(&count));
            return count;
        }
    }

    public uint SequenceCellCount
    {
        get
        {
            uint count;
            Check(Raw->GetSequenceCellCount(&count));
            return count;
        }
    }
}
