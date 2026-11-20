using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nitrox.Model.ModSync;
using Nitrox.Model.Packets;
using NitroxClient.Communication.Abstract;

namespace NitroxClient.ModSync;

/// <summary>
///     Packet transport for mod sync: requests the host's manifest and file chunks over the existing server connection and
///     matches responses to pending requests.
/// </summary>
public sealed class ModSyncClient : IModChunkSource
{
    public static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(30);

    private readonly IPacketSender packetSender;
    private readonly Dictionary<(string Sha256, long Offset), TaskCompletionSource<ModChunk>> pendingChunks = [];
    private readonly object pendingLock = new();
    private TaskCompletionSource<ModManifestResponse> pendingManifest;

    public ModSyncClient(IPacketSender packetSender)
    {
        this.packetSender = packetSender;
    }

    /// <summary>Returns the host's manifest, or null if the server didn't answer (e.g. an older server without mod sync).</summary>
    public async Task<ModManifestResponse> RequestManifestAsync(TimeSpan timeout)
    {
        TaskCompletionSource<ModManifestResponse> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingManifest = request;
        if (!packetSender.Send(new ModManifestRequest()))
        {
            return null;
        }
        Task finished = await Task.WhenAny(request.Task, Task.Delay(timeout));
        return finished == request.Task ? request.Task.Result : null;
    }

    public void OnManifestResponse(ModManifestResponse response) => pendingManifest?.TrySetResult(response);

    public Task<ModChunk> RequestChunkAsync(string sha256, long offset, int length, CancellationToken cancellationToken)
    {
        TaskCompletionSource<ModChunk> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (string, long) key = (sha256, offset);
        lock (pendingLock)
        {
            pendingChunks[key] = request;
        }

        if (!packetSender.Send(new ModFileChunkRequest(sha256, offset, length)))
        {
            Remove(key);
            request.TrySetException(new IOException("Lost connection to the server."));
            return request.Task;
        }

        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ChunkTimeout);
        CancellationTokenRegistration registration = timeout.Token.Register(() =>
        {
            Remove(key);
            if (cancellationToken.IsCancellationRequested)
            {
                request.TrySetCanceled();
            }
            else
            {
                request.TrySetException(new TimeoutException("The server did not answer in time."));
            }
        });
        request.Task.ContinueWith(_ =>
        {
            registration.Dispose();
            timeout.Dispose();
        }, TaskScheduler.Default);
        return request.Task;
    }

    public void OnChunkResponse(ModFileChunkResponse response)
    {
        TaskCompletionSource<ModChunk> request;
        lock (pendingLock)
        {
            if (!pendingChunks.TryGetValue((response.Sha256, response.Offset), out request))
            {
                return; // Late answer to a request that timed out.
            }
            pendingChunks.Remove((response.Sha256, response.Offset));
        }
        request.TrySetResult(new ModChunk(response.Sha256, response.Offset, response.TotalSize, response.Data, response.Error));
    }

    /// <summary>Fails all outstanding requests, e.g. after a disconnect.</summary>
    public void FailAll(string reason)
    {
        List<TaskCompletionSource<ModChunk>> requests;
        lock (pendingLock)
        {
            requests = [..pendingChunks.Values];
            pendingChunks.Clear();
        }
        foreach (TaskCompletionSource<ModChunk> request in requests)
        {
            request.TrySetException(new IOException(reason));
        }
        pendingManifest?.TrySetResult(null);
    }

    private void Remove((string, long) key)
    {
        lock (pendingLock)
        {
            pendingChunks.Remove(key);
        }
    }
}
