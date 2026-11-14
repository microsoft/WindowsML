// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. The ownership model of the sample
// wrapper: each wrapper object owns one reference to a Windows ML COM
// interface and releases it in Dispose, or from the finalizer when the object
// was not disposed. See ..\README.md.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace WinMLSamples.Runtime;

// Implemented by every wrapper type so that As<T> and TryAs<T> can query for it.
public interface IWinMLInterface<TSelf>
    where TSelf : WinMLObject, IWinMLInterface<TSelf>
{
    static abstract Guid InterfaceId { get; }

    // Wraps an interface pointer whose reference the wrapper takes over.
    static abstract TSelf CreateFromOwnedPointer(nint pointer);
}

public abstract unsafe class WinMLObject : IDisposable
{
    private nint _pointer;

    // A reference this object holds on another object it depends on.
    private nint _heldReference;

    // Set on a cached interface, which its parent disposes.
    private bool _ownedByParent;

    private protected WinMLObject(void* pointer)
    {
        _pointer = pointer != null ? (nint)pointer : throw new ArgumentNullException(nameof(pointer));
    }

    ~WinMLObject()
    {
        Release((void*)Interlocked.Exchange(ref _pointer, 0));
        Release((void*)Interlocked.Exchange(ref _heldReference, 0));
    }

    // The interface pointer, without a new reference, for code that also uses
    // the raw bindings. Valid only while this object is alive and not disposed.
    public nint NativePointer => (nint)ThisPtr;

    private protected void* ThisPtr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            nint pointer = _pointer;
            if (pointer == 0)
            {
                ThrowDisposed();
            }

            return (void*)pointer;
        }
    }

    // Queries the object for another interface. The result owns its own
    // reference; dispose it independently.
    public TInterface As<TInterface>()
        where TInterface : WinMLObject, IWinMLInterface<TInterface>
    {
        void* result = null;
        Check(QueryInterface(TInterface.InterfaceId, &result));
        return TInterface.CreateFromOwnedPointer((nint)Required(result));
    }

    // Like As, but returns false when the object does not implement the interface.
    public bool TryAs<TInterface>([NotNullWhen(true)] out TInterface? result)
        where TInterface : WinMLObject, IWinMLInterface<TInterface>
    {
        void* pointer = null;
        HRESULT hr = QueryInterface(TInterface.InterfaceId, &pointer);
        if (hr.Value == WinMLException.NoInterfaceCode)
        {
            result = null;
            return false;
        }

        Check(hr);
        result = TInterface.CreateFromOwnedPointer((nint)Required(pointer));
        return true;
    }

    // Releases the reference. Does nothing for an object that a property of
    // another object returned, such as ExecutionTarget.RawTensorFactory; that
    // object belongs to its parent.
    public void Dispose()
    {
        if (!_ownedByParent)
        {
            DisposeCore();
        }
    }

    private void DisposeCore()
    {
        if (_pointer == 0)
        {
            return;
        }

        OnDisposing();
        Release((void*)Interlocked.Exchange(ref _pointer, 0));
        Release((void*)Interlocked.Exchange(ref _heldReference, 0));
        GC.SuppressFinalize(this);
    }

    // Runs before Dispose releases the reference, never from the finalizer:
    // closes thread-bound objects and disposes cached interfaces.
    private protected virtual void OnDisposing()
    {
    }

    // Keeps other alive for as long as this object, for an object created from
    // it whose dependency the Runtime does not document.
    internal void HoldReference(WinMLObject other)
    {
        void* pointer = other.ThisPtr;
        ((IUnknown*)pointer)->AddRef();
        GC.KeepAlive(other);
        Release((void*)Interlocked.Exchange(ref _heldReference, (nint)pointer));
    }

    // Disposes an interface that GetCachedInterface created.
    private protected static void DisposeCached(WinMLObject? cached)
    {
        cached?.DisposeCore();
    }

    // Keeps this object alive until a call on its pointer has returned, then
    // throws for a failure HRESULT. Success codes, including S_FALSE, do not throw.
    private protected void Check(HRESULT hr)
    {
        GC.KeepAlive(this);
        WinMLException.ThrowIfFailed(hr);
    }

    // Returns a wrapper for another interface of this object, created on first
    // use and disposed with this object. Disposing the returned wrapper does nothing.
    private protected TInterface GetCachedInterface<TInterface>(ref TInterface? field)
        where TInterface : WinMLObject, IWinMLInterface<TInterface>
    {
        TInterface? existing = Volatile.Read(ref field);
        if (existing is not null)
        {
            return existing;
        }

        TInterface created = As<TInterface>();
        created._ownedByParent = true;
        existing = Interlocked.CompareExchange(ref field, created, null);
        if (existing is null)
        {
            return created;
        }

        created.DisposeCore();
        return existing;
    }

    // An interface pointer that a successful call must return.
    private protected static T* Required<T>(T* pointer)
        where T : unmanaged
    {
        return pointer != null ? pointer : throw new InvalidOperationException("The Runtime returned no object.");
    }

    private protected static void* Required(void* pointer)
    {
        return pointer != null ? pointer : throw new InvalidOperationException("The Runtime returned no object.");
    }

    internal static void Release(void* pointer)
    {
        if (pointer != null)
        {
            ((IUnknown*)pointer)->Release();
        }
    }

    private HRESULT QueryInterface(Guid iid, void** result)
    {
        HRESULT hr = ((IUnknown*)ThisPtr)->QueryInterface(&iid, result);
        GC.KeepAlive(this);
        return hr;
    }

    [DoesNotReturn]
    private void ThrowDisposed()
    {
        throw new ObjectDisposedException(GetType().FullName);
    }
}
