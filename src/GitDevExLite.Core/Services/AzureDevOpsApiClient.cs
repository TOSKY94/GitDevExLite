using System.Net.Http.Headers;

namespace GitDevExLite.Core.Services;

public static class AzureDevOpsApiClient
{
    public static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://dev.azure.com/")
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}