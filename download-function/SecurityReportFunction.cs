using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace SsmsClaudeChat.Download;

/// <summary>
/// GET /security/{file}: the security report the Security workflow publishes under <c>security/</c> in the private
/// container. The website's report workflow reads it from here. Only the four published files are reachable, and they
/// hold results and finding counts, never details.
/// </summary>
public sealed class SecurityReportFunction(BlobServiceClient blobs, ILogger<SecurityReportFunction> logger)
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.Ordinal)
    {
        ["security.json"] = "application/json",
        ["badge.json"] = "application/json",
        ["banner.svg"] = "image/svg+xml",
        ["sbom.spdx.json"] = "application/json",
    };

    [Function("SecurityReport")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "head", Route = "security/{file}")] HttpRequestData request,
        string file,
        CancellationToken cancellationToken)
    {
        if (!ContentTypes.TryGetValue(file, out var contentType))
            return request.CreateResponse(HttpStatusCode.NotFound);

        try
        {
            var blob = blobs.GetBlobContainerClient(DownloadFunctions.Container).GetBlobClient($"security/{file}");
            var content = await blob.DownloadContentAsync(cancellationToken);

            var response = request.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", contentType);
            response.Headers.Add("Cache-Control", "public, max-age=300");
            response.Headers.Add("X-Content-Type-Options", "nosniff");
            await response.WriteBytesAsync(content.Value.Content.ToArray(), cancellationToken);
            return response;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return request.CreateResponse(HttpStatusCode.NotFound);
        }
        catch (RequestFailedException ex)
        {
            logger.LogError(ex, "Could not read security/{File} from the {Container} container", file, DownloadFunctions.Container);
            return request.CreateResponse(HttpStatusCode.BadGateway);
        }
    }
}
