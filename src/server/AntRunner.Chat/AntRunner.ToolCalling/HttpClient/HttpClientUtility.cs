using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;

namespace AntRunner.ToolCalling.HttpClient
{
    internal class HttpClientUtility
    {
        private static IHttpClientFactory? _httpClientFactory;

        internal static System.Net.Http.HttpClient Get()
        {
            if (_httpClientFactory == null)
            {
                var serviceProvider = new ServiceCollection().AddHttpClient().BuildServiceProvider();
                _httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
            }
            return _httpClientFactory.CreateClient();
        }
    }
}
