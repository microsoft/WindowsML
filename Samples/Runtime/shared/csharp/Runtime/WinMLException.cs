// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. How the sample wrapper reports errors:
// a failure HRESULT throws a .NET exception whose HResult is the native value.
// Failures with an established .NET meaning throw that type, so most failures
// are not WinMLException; catch by type, or test HResult:
//
//   E_INVALIDARG                               ArgumentException
//   E_POINTER                                  ArgumentNullException
//   E_BOUNDS                                   ArgumentOutOfRangeException
//   E_OUTOFMEMORY                              OutOfMemoryException
//   E_NOTIMPL                                  NotImplementedException
//   E_NOINTERFACE                              InvalidCastException
//   E_ACCESSDENIED                             UnauthorizedAccessException
//   HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)    NotSupportedException
//   HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)   FileNotFoundException
//   HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)   DirectoryNotFoundException
//   HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND)    DllNotFoundException
//   HRESULT_FROM_WIN32(ERROR_CANCELLED)        OperationCanceledException
//   HRESULT_FROM_WIN32(ERROR_TIMEOUT)          TimeoutException
//   HRESULT_FROM_WIN32(ERROR_INVALID_DATA)     InvalidDataException
//   E_NOT_VALID_STATE, E_ILLEGAL_METHOD_CALL,
//   RPC_E_WRONG_THREAD                         InvalidOperationException
//   any other failure                          WinMLException
//
// Success codes, including S_FALSE, never throw. Methods whose S_FALSE has a
// meaning report it through a bool, a Try method, or a null result.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace WinMLSamples.Runtime;

public class WinMLException : Exception
{
    // HRESULT_FROM_WIN32(ERROR_NOT_FOUND).
    public const int NotFound = unchecked((int)0x80070490);

    // E_NOINTERFACE: the object does not implement the queried interface.
    internal const int NoInterfaceCode = unchecked((int)0x80004002);

    // E_FAIL.
    private const int Fail = unchecked((int)0x80004005);

    // Win32 errors that samples also raise themselves; see NotSupported and InvalidData.
    private const int ErrorNotSupported = unchecked((int)0x80070032);
    private const int ErrorInvalidData = unchecked((int)0x8007000D);

    public WinMLException()
    {
    }

    public WinMLException(string? message)
        : base(message)
    {
    }

    public WinMLException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public WinMLException(string? message, int hresult)
        : base(message)
    {
        HResult = hresult;
    }

    // Throws the mapped exception for a failure HRESULT that the call which just
    // returned on this thread reported; success codes return. The message
    // includes any error information that call recorded.
    public static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
        {
            Throw(hresult);
        }
    }

    // Returns the mapped exception for a failure HRESULT from elsewhere, such as
    // a result's error code, with the system message for the HRESULT. A
    // non-failure code maps to E_FAIL.
    public static Exception GetExceptionForHResult(int hresult)
    {
        return Create(hresult < 0 ? hresult : Fail, null, useThreadErrorInfo: false);
    }

    // Returns the mapped exception for a failure HRESULT with a custom message.
    public static Exception GetExceptionForHResult(int hresult, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Create(hresult < 0 ? hresult : Fail, message, useThreadErrorInfo: false);
    }

    private static Exception Create(int hresult, string? message, bool useThreadErrorInfo)
    {
        // -1 tells .NET to ignore the thread's error information, which may
        // belong to an earlier, unrelated failure.
        message ??= Marshal.GetExceptionForHR(hresult, useThreadErrorInfo ? 0 : -1)?.Message
            ?? $"Windows ML Runtime call failed (0x{hresult:X8})";
        Exception exception = unchecked((uint)hresult) switch
        {
            0x80070057 => new ArgumentException(message),
            0x80004003 => new ArgumentNullException(null, message),
            0x8000000B => new ArgumentOutOfRangeException(null, message),
            0x8007000E => new OutOfMemoryException(message),
            0x80004001 => new NotImplementedException(message),
            0x80004002 => new InvalidCastException(message),
            0x80070005 => new UnauthorizedAccessException(message),
            0x80070032 => new NotSupportedException(message),
            0x80070002 => new FileNotFoundException(message),
            0x80070003 => new DirectoryNotFoundException(message),
            0x8007007E => new DllNotFoundException(message),
            0x800704C7 => new OperationCanceledException(message),
            0x800705B4 => new TimeoutException(message),
            0x8007000D => new InvalidDataException(message),
            0x8007139F or 0x8000000E or 0x8001010E => new InvalidOperationException(message),
            _ => new WinMLException(message),
        };

        exception.HResult = hresult;
        return exception;
    }

    // Sample additions. .NET gives NotSupportedException and
    // InvalidDataException HRESULTs of their own; these carry the HRESULT that
    // the Runtime and the C++ samples report for the same condition, so an app
    // reports one code whichever side raised it.
    public static NotSupportedException NotSupported(string message)
    {
        return new NotSupportedException(message) { HResult = ErrorNotSupported };
    }

    public static InvalidDataException InvalidData(string message)
    {
        return new InvalidDataException(message) { HResult = ErrorInvalidData };
    }

    // E_NOINTERFACE, which .NET also uses for InvalidCastException.
    public static InvalidCastException NoInterface(string message)
    {
        return new InvalidCastException(message) { HResult = NoInterfaceCode };
    }

    internal static void ThrowIfFailed(HRESULT hr)
    {
        if (hr.Value < 0)
        {
            Throw(hr.Value);
        }
    }

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw(int hresult)
    {
        throw Create(hresult, null, useThreadErrorInfo: true);
    }
}
