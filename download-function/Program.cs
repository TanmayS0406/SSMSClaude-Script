using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // The same storage account holds the setups (private "downloads" container) and runs the function host.
        // A connection-string client can sign the short-lived download links; a token-credential client could not.
        services.AddSingleton(_ => new BlobServiceClient(
            Environment.GetEnvironmentVariable("AzureWebJobsStorage")
            ?? throw new InvalidOperationException("AzureWebJobsStorage is not set.")));
    })
    .Build();

host.Run();
