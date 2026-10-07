using AutoVpn.Infrastructure.Core;

namespace AutoVpn.UnitTests;

internal static class FileUseInputTransfer
{
    internal static async Task WriteAndCloseAsync(NativeProcessCleanupResources resources,
        ReadOnlyMemory<byte> bytes, Action<FileUseParentPhase> advance, CancellationToken token)
    {
        var writer = resources.StdinWriter ?? throw new InvalidOperationException("OWNED_STDIN_MISSING");
        advance(FileUseParentPhase.INPUT_WRITE);
        // Keep the whole original write+flush operation. WaitAsync only cancels
        // this caller's wait; the owner must still join any remaining native I/O.
        resources.Stdin = WriteAsync();
        await resources.Stdin.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        advance(FileUseParentPhase.INPUT_CLOSE);
        resources.CloseStdin();
        token.ThrowIfCancellationRequested();

        async Task WriteAsync()
        {
            await writer.BaseStream.WriteAsync(bytes, token).ConfigureAwait(false);
            advance(FileUseParentPhase.INPUT_FLUSH);
            // Small FileStream writes can be buffered. Flush is part of the same
            // owned operation so the native write is not deferred to sync Close.
            await writer.BaseStream.FlushAsync(token).ConfigureAwait(false);
        }
    }
}
