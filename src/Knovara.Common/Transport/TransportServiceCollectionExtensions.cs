using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knovara.Common.Transport
{
    public static class TransportServiceCollectionExtensions
    {
        public static IServiceCollection AddTransport(this IServiceCollection services, IConfiguration cfg)
        {
            services.AddHttpClient();
            services.AddSingleton<ITransport, HttpTransport>();
            return services;
        }
    }
}
