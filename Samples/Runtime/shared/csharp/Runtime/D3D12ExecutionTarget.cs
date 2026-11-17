// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. The Direct3D 12 view of a GPU execution
// target, for finding the adapter that the Runtime's device runs on.

using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace WinMLSamples.Runtime;

// Query an ExecutionTarget for it with As or TryAs. CPU targets implement it
// too, but its methods throw NotSupportedException for them, so check
// ExecutionTarget.Kind first.
public sealed unsafe class D3D12ExecutionTarget : WinMLObject, IWinMLInterface<D3D12ExecutionTarget>
{
    private D3D12ExecutionTarget(IWinMLD3D12ExecutionTarget* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLD3D12ExecutionTarget.IID_Guid;

    static D3D12ExecutionTarget IWinMLInterface<D3D12ExecutionTarget>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLD3D12ExecutionTarget*)pointer);

    // The adapter of the target's Direct3D 12 device, from GetDevice and the
    // device's GetAdapterLuid. A target created from an adapter reports that
    // adapter; DXCoreAdapter.GetAdapterByLuid finds it.
    public AdapterLuid GetAdapterLuid()
    {
        IUnknown* unknown = null;
        ID3D12Device* device = null;
        try
        {
            Check(((IWinMLD3D12ExecutionTarget*)ThisPtr)->GetDevice(&unknown));
            Guid deviceIid = ID3D12Device.IID_Guid;
            WinMLException.ThrowIfFailed(Required(unknown)->QueryInterface(&deviceIid, (void**)&device));
            LUID luid = Required(device)->GetAdapterLuid();
            return new AdapterLuid(luid.LowPart, luid.HighPart);
        }
        finally
        {
            Release(device);
            Release(unknown);
        }
    }
}
