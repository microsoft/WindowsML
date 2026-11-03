// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Ownership helper shared by the C# samples. CsWin32 generates the Runtime
// interfaces as unmanaged structs, so the samples release each interface
// pointer they own. ComObjects releases them in reverse creation order.

using Windows.Win32.System.Com;

namespace WinMLSamples;

internal sealed unsafe class ComObjects : IDisposable
{
    private readonly Stack<IntPtr> _objects = new();

    // Takes ownership of one reference to instance and returns it.
    public T* Add<T>(T* instance)
        where T : unmanaged
    {
        if (instance != null)
        {
            _objects.Push((IntPtr)instance);
        }

        return instance;
    }

    // Queries source for TInterface and takes ownership of the result.
    public TInterface* QueryInterface<TInterface>(void* source)
        where TInterface : unmanaged
    {
        TInterface* result;
        Guid iid = typeof(TInterface).GUID;
        ((IUnknown*)source)->QueryInterface(&iid, (void**)&result).ThrowOnFailure();
        return Add(result);
    }

    // Like QueryInterface, but returns null when source does not implement it.
    public TInterface* TryQueryInterface<TInterface>(void* source)
        where TInterface : unmanaged
    {
        TInterface* result = null;
        Guid iid = typeof(TInterface).GUID;
        return ((IUnknown*)source)->QueryInterface(&iid, (void**)&result).Succeeded ? Add(result) : null;
    }

    // The Runtime metadata projects [in] IUnknown* parameters, such as the
    // adapter of IWinMLRuntime::CreateExecutionTargetFromAdapter, as IUnknown
    // values. IUnknown holds only its vtable pointer, so a value whose bits are
    // the interface pointer passes exactly the pointer the native method expects.
    public static IUnknown AsUnknownArgument(void* instance)
    {
        return *(IUnknown*)&instance;
    }

    public void Dispose()
    {
        while (_objects.Count > 0)
        {
            ((IUnknown*)_objects.Pop())->Release();
        }
    }
}
