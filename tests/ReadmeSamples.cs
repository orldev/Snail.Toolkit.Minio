using Microsoft.Extensions.Logging;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// The readme's samples, compiled.
/// </summary>
/// <remarks>
/// Documentation that does not compile is worse than none: it is confidently wrong. These are the readme's
/// snippets, verbatim enough that a change breaking them breaks the build.
/// </remarks>
public sealed class ReadmeSamples(IObjectStorage storage, IBuckets buckets, ILogger<ReadmeSamples> logger)
{
    public async Task<string?> SaveAsync(Stream file, string name, CancellationToken token)
    {
        var stored = await storage.PutAsync(
            "reports",
            file,
            new UploadOptions { Name = name, MediaType = "application/pdf" },
            token);

        if (!stored.TryGetValue(out var report))
        {
            logger.LogWarning("upload failed: {Error}", stored.Error);

            return null;
        }

        return report.Name;
    }

    public async Task<StorageErrorKind?> ReadAsync(string name, Stream destination, CancellationToken token)
    {
        var read = await storage.GetAsync("reports", name, token);

        if (!read.TryGetValue(out var content))
            return read.Error.Kind;

        await using (content)
        {
            await content.Stream.CopyToAsync(destination, token);
        }

        return null;
    }

    public async Task WalkAsync(CancellationToken token)
    {
        var page = await storage.ListAsync("reports", new ListOptions { Prefix = "2026/", PageSize = 50 }, token);

        while (page.Value.HasMore)
        {
            page = await storage.ListAsync(
                "reports",
                new ListOptions { Prefix = "2026/", PageSize = 50, Cursor = page.Value.Cursor },
                token);
        }

        await foreach (var item in storage.EnumerateAsync("reports", cancellationToken: token))
        {
            if (!item.TryGetValue(out _))
            {
                logger.LogWarning("listing stopped: {Error}", item.Error);

                break;
            }
        }
    }

    public async Task<long> ShareAsync(string name, CancellationToken token)
    {
        var link = await storage.SignedUrlAsync("reports", name, TimeSpan.FromMinutes(10), token);
        var slot = await storage.SignedUploadUrlAsync("uploads", name, TimeSpan.FromMinutes(5), token);

        logger.LogInformation("{Read} {Write}", link.Value, slot.Value);

        await buckets.CreateAsync("reports", token);

        var exists = await storage.ExistsAsync("reports", name, token);

        return exists.Value
            ? await storage.StatAsync("reports", name, token)
                .MatchAsync(onSuccess: report => report.Size, onFailure: _ => 0L)
            : 0L;
    }

    public async Task KeepAsync(CancellationToken token)
    {
        await storage.CopyAsync("uploads", "tmp/9f2c", "reports", "q3.pdf", token);
        await storage.RemoveAsync("uploads", "tmp/9f2c", token);
    }

    public async Task SweepAsync(CancellationToken token)
    {
        await buckets.SetExpiryAsync(
            "uploads",
            [
                new ExpiryRule { Id = "staged", Prefix = "tmp/", Days = 1 }
            ],
            token);

        var rules = await buckets.GetExpiryAsync("uploads", token);

        logger.LogInformation("{Rules} rules", rules.Value.Count);
    }
}
