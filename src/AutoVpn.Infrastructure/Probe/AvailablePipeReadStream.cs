using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AutoVpn.Infrastructure.Probe;

// One exclusive reader, for a fresh owned Process.StandardOutput/Error handle only.
// null means idle, zero means EOF. The poller must never start an empty blocking read.
internal interface IAvailablePipeReader
{
    int? ReadAvailable(byte[] buffer, int count);
}

/// <summary>
/// Avoids parking ThreadPool workers in synchronous Windows anonymous-pipe reads.
/// The Process owns the handle. This adapter never closes it or reads concurrently.
/// </summary>
internal sealed class AvailablePipeReadStream(IAvailablePipeReader source) : Stream
{
    private readonly byte[] _buffer = new byte[4096];
    private int _reading;
    private bool _disposed;

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (destination.Length == 0) return 0;
        if (Interlocked.Exchange(ref _reading, 1) != 0)
            throw new InvalidOperationException("OWNED_PIPE_CONCURRENT_READ");
        try
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                var requested = Math.Min(destination.Length, _buffer.Length);
                var count = source.ReadAvailable(_buffer, requested);
                if (count is not null)
                {
                    if (count.Value < 0 || count.Value > requested)
                        throw new IOException("OWNED_PIPE_INVALID_COUNT");
                    _buffer.AsMemory(0, count.Value).CopyTo(destination);
                    return count.Value;
                }
                // Not a remote probe retry and not an added shutdown budget.
                // No OS read is outstanding while the pipe is empty.
                await WaitForPollAsync(token).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private static async Task WaitForPollAsync(CancellationToken token)
    {
        // Task.Delay deliberately queues its cancellation continuation. An owned
        // idle read must also unwind when unrelated work occupies every worker.
        // Cancellation deliberately permits inline continuations. Owned callers
        // cancel outside lifecycle locks; this reader then unwinds without another
        // native read. A late timer can touch only this completed signal.
        var signal = new TaskCompletionSource();
        using var timer = new Timer(static state => ((TaskCompletionSource)state!).TrySetResult(),
            signal, 10, Timeout.Infinite);
        using var registration = token.UnsafeRegister(static (state, canceled) =>
            ((TaskCompletionSource)state!).TrySetCanceled(canceled), signal);
        await signal.Task.ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    internal static AvailablePipeReadStream ForOwnedHandle(SafeFileHandle handle) => new(new NativeReader(handle));

    [SupportedOSPlatform("windows")]
    private sealed class NativeReader : IAvailablePipeReader
    {
        private readonly SafeFileHandle _handle;
        internal NativeReader(SafeFileHandle handle)
        {
            if (handle.IsClosed || handle.IsInvalid || Native.GetFileType(handle) != 3)
                throw new IOException("OWNED_PIPE_HANDLE_REQUIRED");
            _handle = handle;
        }
        public int? ReadAvailable(byte[] buffer, int count)
        {
            if (!Native.PeekNamedPipe(_handle, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero))
                return EndOrThrow(Marshal.GetLastWin32Error());
            if (available == 0) return null;
            // Only this adapter consumes the handle. Request no more than the bytes
            // already present, so a quiet sibling cannot occupy a ThreadPool thread.
            var requested = (uint)Math.Min(count, available);
            if (!Native.ReadFile(_handle, buffer, requested, out var read, IntPtr.Zero))
                return EndOrThrow(Marshal.GetLastWin32Error());
            return checked((int)read);
        }
        private static int EndOrThrow(int error)
        {
            if (error is 109 or 233) return 0; // ERROR_BROKEN_PIPE / ERROR_PIPE_NOT_CONNECTED
            throw new IOException("OWNED_PIPE_IO", unchecked((int)(0x80070000u | (uint)error)));
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekNamedPipe(SafeFileHandle pipe, IntPtr buffer, uint size,
            IntPtr read, out uint available, IntPtr remaining);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(SafeFileHandle handle, [Out] byte[] buffer, uint size,
            out uint read, IntPtr overlapped);
    }

    protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
