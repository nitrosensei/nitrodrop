// Nitro.Awdl: Win32 device access (cfgmgr32 interface enumeration, CreateFile, overlapped DeviceIoControl).
// Copyright (c) 2026 Nitrobyte.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Nitro.Awdl.Interop;

[SupportedOSPlatform("windows")]
internal static unsafe partial class DeviceNative
{
    private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    private const uint CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    internal const int ERROR_IO_PENDING = 997, ERROR_OPERATION_ABORTED = 995, ERROR_BUSY = 170, ERROR_NOT_SUPPORTED = 50,
        ERROR_ACCESS_DENIED = 5, ERROR_FILE_NOT_FOUND = 2, ERROR_SHARING_VIOLATION = 32, ERROR_INVALID_FUNCTION = 1;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static partial uint CM_Get_Device_Interface_List_Size(out uint len, in Guid cls, nint deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static partial uint CM_Get_Device_Interface_List(in Guid cls, nint deviceId, char* buffer, uint len, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuf, uint inLen, void* outBuf, uint outLen,
        uint* returned, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);

    /// <summary>Present device interface paths for <paramref name="cls"/>.</summary>
    internal static List<string> InterfacePaths(Guid cls)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint cr = CM_Get_Device_Interface_List_Size(out uint len, in cls, 0, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (cr != CR_SUCCESS) throw new Win32Exception($"CM_Get_Device_Interface_List_Size failed (CR 0x{cr:x})");
            if (len <= 1) return [];
            var buf = new char[len];
            fixed (char* p = buf)
                cr = CM_Get_Device_Interface_List(in cls, 0, p, len, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (cr == CR_BUFFER_SMALL) continue; // a device arrived in between
            if (cr != CR_SUCCESS) throw new Win32Exception($"CM_Get_Device_Interface_List failed (CR 0x{cr:x})");
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        throw new Win32Exception("device interface list kept changing");
    }

    internal static SafeFileHandle Open(string path)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, 0, 0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw err switch
            {
                ERROR_ACCESS_DENIED => new UnauthorizedAccessException(
                    $"{path}: access denied. The driver's ACL admits SYSTEM and Administrators only (airdropwifi.inf)."),
                ERROR_SHARING_VIOLATION => new IOException($"{path}: already open (the device is exclusive, one handle)."),
                _ => new Win32Exception(err, $"CreateFile({path}) failed"),
            };
        }
        return h;
    }
}

/// <summary>Overlapped DeviceIoControl on a handle bound to the thread pool's completion port.</summary>
[SupportedOSPlatform("windows")]
internal sealed class OverlappedDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly ThreadPoolBoundHandle _bound;
    private readonly IOCompletionCallback _callback;

    private sealed class Op
    {
        public readonly TaskCompletionSource<(uint Error, uint Bytes)> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public nint In, Out, Overlapped;
        public int OutCap;
    }

    public unsafe OverlappedDevice(SafeFileHandle handle)
    {
        _handle = handle;
        _bound = ThreadPoolBoundHandle.BindHandle(handle);
        _callback = OnComplete;
    }

    private unsafe void OnComplete(uint error, uint bytes, NativeOverlapped* ov)
    {
        var op = (Op)ThreadPoolBoundHandle.GetNativeOverlappedState(ov)!;
        op.Done.TrySetResult((error, bytes));
    }

    /// <summary>Issues one IOCTL and returns its output (truncated to the bytes returned).</summary>
    public async Task<byte[]> IoctlAsync(uint code, byte[]? input, int outCap, CancellationToken ct = default)
    {
        var op = Issue(code, input, outCap, out int syncError);
        if (op == null) throw ToException(syncError, code);
        try
        {
            (uint err, uint n) r;
            using (ct.Register(() => Cancel(op)))
                r = await op.Done.Task.ConfigureAwait(false);
            if (r.err != 0)
            {
                if (r.err == DeviceNative.ERROR_OPERATION_ABORTED) throw new OperationCanceledException(ct);
                throw ToException((int)r.err, code);
            }
            return CopyOut(op, (int)r.n);
        }
        finally
        {
            Free(op);
        }
    }

    private static Exception ToException(int err, uint code) => err switch
    {
        DeviceNative.ERROR_BUSY => new IOException($"IOCTL 0x{code:x8}: device busy (TX ring full)", unchecked((int)0x800700AA)),
        DeviceNative.ERROR_INVALID_FUNCTION or DeviceNative.ERROR_NOT_SUPPORTED =>
            new NotSupportedException($"IOCTL 0x{code:x8} not implemented by this driver build (Win32 {err})"),
        _ => new Win32Exception(err, $"IOCTL 0x{code:x8} failed"),
    };

    private unsafe Op? Issue(uint code, byte[]? input, int outCap, out int syncError)
    {
        var op = new Op { OutCap = outCap };
        if (input is { Length: > 0 })
        {
            op.In = (nint)NativeMemory.Alloc((nuint)input.Length);
            input.CopyTo(new Span<byte>((void*)op.In, input.Length));
        }
        if (outCap > 0) op.Out = (nint)NativeMemory.AllocZeroed((nuint)outCap);
        var ov = _bound.AllocateNativeOverlapped(_callback, op, null);
        op.Overlapped = (nint)ov;
        bool ok = DeviceNative.DeviceIoControl(_handle, code, (void*)op.In, (uint)(input?.Length ?? 0), (void*)op.Out, (uint)outCap, null, ov);
        syncError = ok ? 0 : Marshal.GetLastPInvokeError();
        if (!ok && syncError != DeviceNative.ERROR_IO_PENDING)
        {
            // No completion packet is queued for an immediate failure.
            Free(op);
            return null;
        }
        return op; // pending, or completed synchronously: the completion port reports both
    }

    private unsafe void Cancel(Op op)
    {
        if (op.Overlapped != 0) DeviceNative.CancelIoEx(_handle, (NativeOverlapped*)op.Overlapped);
    }

    private static unsafe byte[] CopyOut(Op op, int n) =>
        op.Out == 0 ? [] : new ReadOnlySpan<byte>((void*)op.Out, Math.Min(n, op.OutCap)).ToArray();

    private unsafe void Free(Op op)
    {
        if (op.Overlapped != 0)
        {
            _bound.FreeNativeOverlapped((NativeOverlapped*)op.Overlapped);
            op.Overlapped = 0;
        }
        if (op.In != 0) { NativeMemory.Free((void*)op.In); op.In = 0; }
        if (op.Out != 0) { NativeMemory.Free((void*)op.Out); op.Out = 0; }
    }

    public void Dispose()
    {
        _bound.Dispose();
        _handle.Dispose();
    }
}
