using System;
using System.Net.Http;

namespace OpenSim.Framework;

// Used as below:
//
// public async Task<HttpResponseMessage> MakeRequestAsync(string requestUrl)
// {
//     HttpClient client = HttpClientFactory.Instance;
//     HttpResponseMessage response = await client.GetAsync(requestUrl);
//     return response;
// }

public class HttpClientFactory
{
    private static readonly Lazy<HttpClient> lazyClient = new Lazy<HttpClient>(() => CreateHttpClient());
    public static HttpClient Instance => lazyClient.Value;

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new HttpClient();

        // Configure the HttpClient instance if necessary
        // client.Timeout = TimeSpan.FromSeconds(30);
        // client.DefaultRequestHeaders.Add("HeaderName", "HeaderValue");

        return client;
    }
}
