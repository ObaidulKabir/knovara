using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knovara.Common.Transport
{
    public static class TransportServiceCollectionExtensions
    {
        public static IServiceCollection AddTransport(this IServiceCollection services, IConfiguration cfg)
        {
            var useMcp = cfg.GetValue<bool>("UseMcp", false);
            services.AddHttpClient();

            if (useMcp)
            {
                services.AddSingleton<ITransport, McpTransport>();
            }
            else
            {
                services.AddSingleton<ITransport, HttpTransport>();
            }

            return services;
        }
    }
}
