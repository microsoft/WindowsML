// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Tensors and tensor factories. A tensor's
// data is read and written through TensorLock<T>, a Span<T> over the locked
// memory: a CPU tensor's data is not copied.

using Microsoft.Windows.AI.MachineLearning.Runtime;

namespace WinMLSamples.Runtime;

// Maps .NET element types to TensorDataType.
public static class TensorElementType
{
    public static TensorDataType Of<T>()
        where T : unmanaged
    {
        return typeof(T) == typeof(float) ? TensorDataType.Float32
            : typeof(T) == typeof(Half) ? TensorDataType.Float16
            : typeof(T) == typeof(double) ? TensorDataType.Float64
            : typeof(T) == typeof(sbyte) ? TensorDataType.Int8
            : typeof(T) == typeof(byte) ? TensorDataType.UInt8
            : typeof(T) == typeof(short) ? TensorDataType.Int16
            : typeof(T) == typeof(ushort) ? TensorDataType.UInt16
            : typeof(T) == typeof(int) ? TensorDataType.Int32
            : typeof(T) == typeof(uint) ? TensorDataType.UInt32
            : typeof(T) == typeof(long) ? TensorDataType.Int64
            : typeof(T) == typeof(ulong) ? TensorDataType.UInt64
            : typeof(T) == typeof(bool) ? TensorDataType.Bool
            : TensorDataType.Undefined;
    }

    internal static TensorDataType Require<T>()
        where T : unmanaged
    {
        TensorDataType dataType = Of<T>();
        return dataType != TensorDataType.Undefined
            ? dataType
            : throw new NotSupportedException($"{typeof(T)} is not a tensor element type.");
    }
}

// CPU access to a tensor's elements. Take it with Tensor.Lock<T> in a using
// statement; Dispose releases the lock. For a CPU tensor, Span is the tensor's
// own memory; for a device tensor locked with AllowSynchronizedCpuAccess, it is
// a staged copy. Do not copy the lock: each copy would release it again.
public unsafe ref struct TensorLock<T>
    where T : unmanaged
{
    private IWinMLTensorDataLock* _lock;
    private readonly Span<T> _span;

    internal TensorLock(IWinMLTensorDataLock* dataLock, Span<T> span)
    {
        _lock = dataLock;
        _span = span;
    }

    // Write only through a lock taken with a write mode.
    public readonly Span<T> Span => _lock != null ? _span : throw new ObjectDisposedException(nameof(TensorLock<T>));

    public readonly int Length => _span.Length;

    // The elements, read-only, for a lock taken to read.
    public readonly ReadOnlySpan<T> AsReadOnlySpan() => Span;

    // For a writable lock on a device tensor, makes the staged writes visible
    // to the device. Releasing such a lock without Commit discards them. Other
    // locks ignore it.
    public readonly void Commit()
    {
        if (_lock == null)
        {
            throw new ObjectDisposedException(nameof(TensorLock<T>));
        }

        IWinMLTensorSynchronizedDataLock* synchronized = null;
        Guid iid = IWinMLTensorSynchronizedDataLock.IID_Guid;
        if (((Windows.Win32.System.Com.IUnknown*)_lock)->QueryInterface(&iid, (void**)&synchronized).Value < 0)
        {
            return;
        }

        int hr = synchronized->Commit().Value;
        WinMLObject.Release(synchronized);
        WinMLException.ThrowIfFailed(hr);
    }

    public void Dispose()
    {
        IWinMLTensorDataLock* dataLock = _lock;
        _lock = null;
        WinMLObject.Release(dataLock);
    }
}

public sealed unsafe class Tensor : WinMLObject, IWinMLInterface<Tensor>
{
    internal Tensor(IWinMLTensor* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTensor.IID_Guid;

    static Tensor IWinMLInterface<Tensor>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTensor*)pointer);

    public TensorDataType DataType => (TensorDataType)GetDesc().dataType;

    public long[] Shape
    {
        get
        {
            WINML_TENSOR_DESC desc = GetDesc();
            long[] shape = new long[desc.dimensionCount];
            for (int i = 0; i < shape.Length; ++i)
            {
                shape[i] = checked((long)desc.dimensions[i]);
            }

            GC.KeepAlive(this);
            return shape;
        }
    }

    // The product of the dimensions.
    public long ElementCount
    {
        get
        {
            WINML_TENSOR_DESC desc = GetDesc();
            long count = 1;
            for (uint i = 0; i < desc.dimensionCount; ++i)
            {
                count = checked(count * (long)desc.dimensions[i]);
            }

            GC.KeepAlive(this);
            return count;
        }
    }

    internal IWinMLTensor* Raw => (IWinMLTensor*)ThisPtr;

    // Locks the tensor for CPU access and returns its elements as a span. T
    // must match DataType, or be byte for raw bytes. A CPU tensor's span is its
    // own memory. AllowSynchronizedCpuAccess permits a staged copy of a device
    // tensor; a device tensor locked without it throws NotSupportedException.
    public TensorLock<T> Lock<T>(TensorLockMode mode = TensorLockMode.Read, TensorLockFlags flags = TensorLockFlags.None)
        where T : unmanaged
    {
        if (typeof(T) != typeof(byte))
        {
            TensorDataType expected = TensorElementType.Require<T>();
            TensorDataType actual = DataType;
            if (expected != actual)
            {
                throw new InvalidCastException($"The tensor holds {actual} elements, not {typeof(T).Name}.");
            }
        }

        IWinMLTensorDataLock* dataLock = null;
        Check(Raw->Lock((WINML_TENSOR_LOCK_MODE)mode, (WINML_TENSOR_LOCK_FLAGS)flags, &dataLock));
        byte* data = null;
        ulong byteCount = 0;
        int hr = Required(dataLock)->GetData(&data, &byteCount).Value;
        if (hr < 0)
        {
            WinMLObject.Release(dataLock);
            WinMLException.ThrowIfFailed(hr);
        }

        return new TensorLock<T>(dataLock, new Span<T>(data, checked((int)(byteCount / (ulong)sizeof(T)))));
    }

    internal static TensorDescription ToDescription(in WINML_TENSOR_DESC desc)
    {
        return new TensorDescription((TensorDataType)desc.dataType, new ReadOnlySpan<ulong>(desc.dimensions, (int)desc.dimensionCount).ToArray());
    }

    // The dimensions point into the tensor's own storage.
    private WINML_TENSOR_DESC GetDesc()
    {
        WINML_TENSOR_DESC desc;
        Check(Raw->GetDesc(&desc));
        return desc;
    }
}

// Creates tensors over raw element data, and views of regions of tensors.
public sealed unsafe class RawTensorFactory : WinMLObject, IWinMLInterface<RawTensorFactory>
{
    private RawTensorFactory(IWinMLRawTensorFactory* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLRawTensorFactory.IID_Guid;

    static RawTensorFactory IWinMLInterface<RawTensorFactory>.CreateFromOwnedPointer(nint pointer) => new((IWinMLRawTensorFactory*)pointer);

    private IWinMLRawTensorFactory* Self => (IWinMLRawTensorFactory*)ThisPtr;

    // A tensor that views part of source: extents[axis] elements from
    // starts[axis] on each axis.
    public Tensor CreateTensorFromRegion(Tensor source, ReadOnlySpan<uint> starts, ReadOnlySpan<uint> extents)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (starts.Length != extents.Length)
        {
            throw new ArgumentException("starts and extents must have one value per axis.");
        }

        IWinMLTensor* tensor = null;
        fixed (uint* startPointer = starts)
        fixed (uint* extentPointer = extents)
        {
            Check(Self->CreateTensorFromRegion(source.Raw, startPointer, extentPointer, (uint)starts.Length, &tensor));
        }

        GC.KeepAlive(source);
        return new Tensor(Required(tensor));
    }

    internal Tensor CreateTensor(TensorDataType dataType, ReadOnlySpan<long> shape, void* data, ulong byteCount)
    {
        Span<ulong> dimensions = shape.Length <= 8 ? stackalloc ulong[shape.Length] : new ulong[shape.Length];
        for (int i = 0; i < shape.Length; ++i)
        {
            dimensions[i] = shape[i] >= 0 ? (ulong)shape[i] : throw new ArgumentOutOfRangeException(nameof(shape), "Tensor dimensions must not be negative.");
        }

        IWinMLTensor* tensor = null;
        fixed (ulong* dimensionPointer = dimensions)
        {
            WINML_TENSOR_DESC desc = new()
            {
                dataType = (WINML_TENSOR_DATA_TYPE)dataType,
                dimensionCount = (uint)shape.Length,
                dimensions = dimensionPointer,
            };
            Check(Self->CreateTensor(&desc, data, byteCount, &tensor));
        }

        return new Tensor(Required(tensor));
    }
}

// Converts images in memory to tensors: resizes, reorders channels,
// normalizes, and repacks in one call.
public sealed unsafe class ImageTensorFactory : WinMLObject, IWinMLInterface<ImageTensorFactory>
{
    private ImageTensorFactory(IWinMLImageTensorFactory* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLImageTensorFactory.IID_Guid;

    static ImageTensorFactory IWinMLInterface<ImageTensorFactory>.CreateFromOwnedPointer(nint pointer) => new((IWinMLImageTensorFactory*)pointer);

    // pixelFormat is a DXGI_FORMAT value, such as 87 for DXGI_FORMAT_B8G8R8A8_UNORM.
    public Tensor CreateTensorFromImageMemory(
        ReadOnlySpan<byte> pixels,
        uint width,
        uint height,
        uint rowPitch,
        uint pixelFormat,
        ImageTensorDescription description)
    {
        WINML_IMAGE_TENSOR_DESC desc = new()
        {
            dataType = (WINML_TENSOR_DATA_TYPE)description.DataType,
            layout = (WINML_TENSOR_LAYOUT_FORMAT)description.Layout,
            channelOrder = (WINML_TENSOR_CHANNEL_ORDER)description.ChannelOrder,
            batchSize = description.BatchSize,
            channels = description.Channels,
            width = description.Width,
            height = description.Height,
            resizeMode = (WINML_IMAGE_RESIZE_MODE)description.ResizeMode,
        };

        TensorNormalization normalization = description.Normalization;
        ReadOnlySpan<float> scale = normalization.Scale;
        ReadOnlySpan<float> mean = normalization.Mean;
        scale.CopyTo(desc.normalization.scale.AsSpan());
        mean.CopyTo(desc.normalization.mean.AsSpan());

        IWinMLTensor* tensor = null;
        fixed (byte* data = pixels)
        {
            Check(((IWinMLImageTensorFactory*)ThisPtr)->CreateTensorFromImageMemory(
                data, (ulong)pixels.Length, width, height, rowPitch, pixelFormat, &desc, &tensor));
        }

        return new Tensor(Required(tensor));
    }
}

public sealed unsafe partial class ExecutionTarget
{
    private RawTensorFactory? _rawTensorFactory;
    private ImageTensorFactory? _imageTensorFactory;

    public RawTensorFactory RawTensorFactory => GetCachedInterface(ref _rawTensorFactory);

    public ImageTensorFactory ImageTensorFactory => GetCachedInterface(ref _imageTensorFactory);

    // Creates a tensor that holds a copy of data, in row-major order. A target
    // pinned to an execution provider does not create tensors; create them on
    // its hardware target or on a CPU target.
    public Tensor CreateTensor<T>(ReadOnlySpan<T> data, ReadOnlySpan<long> shape)
        where T : unmanaged
    {
        TensorDataType dataType = TensorElementType.Require<T>();
        fixed (T* pointer = data)
        {
            return RawTensorFactory.CreateTensor(dataType, shape, pointer, (ulong)data.Length * (ulong)sizeof(T));
        }
    }

    private protected override void OnDisposing()
    {
        DisposeCached(_rawTensorFactory);
        DisposeCached(_imageTensorFactory);
    }
}
