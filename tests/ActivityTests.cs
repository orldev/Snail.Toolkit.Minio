using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio.DataModel;
using Snail.Toolkit.Minio.Adapters;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// What an operation records when something listens: an activity named for the operation, tagged with what it addressed.
/// </summary>
public class ActivityTests
{
    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Content-Type"] = "image/png",
        ["Content-Length"] = "1024",
        ["Last-Modified"] = "Mon, 12 Oct 2026 10:00:00 GMT"
    };

    [Fact]
    public async Task Stat_Listened_RecordsTheOperation()
    {
        var bucket = $"activity-{Guid.NewGuid():N}";
        var recorded = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MinioObjectStorage.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("storage.bucket"), bucket))
                    recorded.Add(activity);
            }
        };

        ActivitySource.AddActivityListener(listener);

        using var storage = new MinioObjectStorage(
            "Minio",
            MetadataMappingTests.Answers.Stat(ObjectStat.FromResponseHeaders("a.png", Headers)),
            new HttpClient(),
            new Settings(),
            NullLogger<MinioObjectStorage>.Instance);

        await storage.StatAsync(bucket, "a.png");

        var activity = Assert.Single(recorded);

        Assert.Equal("storage.stat", activity.OperationName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal("minio", activity.GetTagItem("storage.system"));
        Assert.Equal("Minio", activity.GetTagItem("storage.configuration"));
        Assert.Equal("a.png", activity.GetTagItem("storage.object"));
    }

    private sealed class Settings : IOptionsMonitor<MinioOptions>
    {
        public MinioOptions CurrentValue { get; } = new();

        public MinioOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<MinioOptions, string?> listener) => null;
    }
}
