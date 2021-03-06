﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschraenkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Squidex.Infrastructure.Tasks;

namespace Squidex.Infrastructure.Assets
{
    public class MemoryAssetStore : IAssetStore
    {
        private readonly ConcurrentDictionary<string, MemoryStream> streams = new ConcurrentDictionary<string, MemoryStream>();
        private readonly AsyncLock readerLock = new AsyncLock();
        private readonly AsyncLock writerLock = new AsyncLock();

        public string? GeneratePublicUrl(string fileName)
        {
            return null;
        }

        public virtual async Task CopyAsync(string sourceFileName, string targetFileName, CancellationToken ct = default)
        {
            Guard.NotNullOrEmpty(sourceFileName);
            Guard.NotNullOrEmpty(targetFileName);

            if (!streams.TryGetValue(sourceFileName, out var sourceStream))
            {
                throw new AssetNotFoundException(sourceFileName);
            }

            using (await readerLock.LockAsync())
            {
                await UploadAsync(targetFileName, sourceStream, false, ct);
            }
        }

        public virtual async Task DownloadAsync(string fileName, Stream stream, BytesRange range = default, CancellationToken ct = default)
        {
            Guard.NotNullOrEmpty(fileName);
            Guard.NotNull(stream);

            if (!streams.TryGetValue(fileName, out var sourceStream))
            {
                throw new AssetNotFoundException(fileName);
            }

            using (await readerLock.LockAsync())
            {
                try
                {
                    await sourceStream.CopyToAsync(stream, range, ct);
                }
                finally
                {
                    sourceStream.Position = 0;
                }
            }
        }

        public virtual async Task UploadAsync(string fileName, Stream stream, bool overwrite = false, CancellationToken ct = default)
        {
            Guard.NotNullOrEmpty(fileName);
            Guard.NotNull(stream);

            var memoryStream = new MemoryStream();

            async Task CopyAsync()
            {
                using (await writerLock.LockAsync())
                {
                    try
                    {
                        await stream.CopyToAsync(memoryStream, 81920, ct);
                    }
                    finally
                    {
                        memoryStream.Position = 0;
                    }
                }
            }

            if (overwrite)
            {
                await CopyAsync();

                streams[fileName] = memoryStream;
            }
            else if (streams.TryAdd(fileName, memoryStream))
            {
                await CopyAsync();
            }
            else
            {
                throw new AssetAlreadyExistsException(fileName);
            }
        }

        public virtual Task DeleteAsync(string fileName)
        {
            Guard.NotNullOrEmpty(fileName);

            streams.TryRemove(fileName, out _);

            return Task.CompletedTask;
        }
    }
}
