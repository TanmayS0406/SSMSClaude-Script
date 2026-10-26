using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SsmsClaudeChat.Download;

/// <summary>One counted download: which version, and when the visitor clicked.</summary>
public sealed record DownloadEvent(string Version, DateTime AtUtc);

/// <summary>The reply to a download request, plus the queue message that counts it.</summary>
public sealed class DownloadResult
{
    [QueueOutput(DownloadFunctions.CountQueue, Connection = "AzureWebJobsStorage")]
    public string? Count { get; set; }

    [HttpResult]
    public required HttpResponseData Response { get; set; }
}

public sealed partial class DownloadFunctions(BlobServiceClient blobs, IConfiguration config, ILogger<DownloadFunctions> logger)
{
    public const string CountQueue = "download-counts";

    internal const string Container = "downloads";

    /// <summary>How long a visitor has to start the download once redirected.</summary>
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(5);

    // Both values come from version.json, which our own pipeline writes; checking the shape keeps a bad file
    // from turning into a link to some other blob.
    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionShape();

    [GeneratedRegex(@"^\d+\.\d+\.\d+/SsmsClaudeChat-Setup-\d+\.\d+\.\d+\.exe$")]
    private static partial Regex PathShape();

    /// <summary>
    /// GET /download: redirects to a five-minute link for the newest setup. The file itself is sent by blob storage,
    /// so the function only spends a few milliseconds per download.
    /// </summary>
    [Function("Download")]
    public async Task<DownloadResult> Download(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "head", Route = "download")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var container = blobs.GetBlobContainerClient(Container);
        var (version, path) = await ReadLatestAsync(container, cancellationToken);
        if (version is null || path is null)
            return new DownloadResult { Response = request.CreateResponse(HttpStatusCode.ServiceUnavailable) };

        var fileName = path[(path.IndexOf('/') + 1)..];
        var link = container.GetBlobClient(path).GenerateSasUri(new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow + LinkLifetime)
        {
            BlobContainerName = Container,
            BlobName = path,
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5), // tolerate clock skew
            Protocol = SasProtocol.Https,
            ContentDisposition = $"attachment; filename=\"{fileName}\"",
            ContentType = "application/octet-stream",
        });

        var response = request.CreateResponse(HttpStatusCode.Found);
        response.Headers.Add("Location", link.AbsoluteUri);
        // The newest version changes with every release and the link expires, so nothing may keep this redirect.
        response.Headers.Add("Cache-Control", "no-store");

        // HEAD requests (link checkers, previews) are not downloads.
        var counted = string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase);
        return new DownloadResult
        {
            Response = response,
            Count = counted ? JsonSerializer.Serialize(new DownloadEvent(version, DateTime.UtcNow)) : null,
        };
    }

    /// <summary>
    /// Writes a counted download to SQL. This runs from a queue, not from the request, because the free serverless
    /// database pauses when idle and takes up to a minute to resume; a failed attempt just goes back on the queue.
    /// </summary>
    [Function("RecordDownload")]
    public async Task Record(
        [QueueTrigger(CountQueue, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken cancellationToken)
    {
        var download = JsonSerializer.Deserialize<DownloadEvent>(message)
            ?? throw new InvalidOperationException("Empty download message.");

        await using var connection = new SqlConnection(config["SqlConnection"]);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT dbo.Downloads (Version, DownloadedAtUtc) VALUES (@version, @at)";
        command.Parameters.AddWithValue("@version", download.Version);
        command.Parameters.AddWithValue("@at", download.AtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(string? Version, string? Path)> ReadLatestAsync(BlobContainerClient container, CancellationToken cancellationToken)
    {
        try
        {
            var content = await container.GetBlobClient("version.json").DownloadContentAsync(cancellationToken);
            using var json = JsonDocument.Parse(content.Value.Content);
            var version = json.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            var path = json.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (version is not null && path is not null && VersionShape().IsMatch(version) && PathShape().IsMatch(path))
                return (version, path);

            logger.LogError("version.json has an unexpected shape: version={Version} path={Path}", version, path);
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or JsonException)
        {
            logger.LogError(ex, "Could not read version.json from the {Container} container", Container);
        }
        return (null, null);
    }
}
