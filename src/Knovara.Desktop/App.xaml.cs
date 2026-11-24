using System;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Knovara.Common.Transport;

namespace Knovara.Desktop
{
    public partial class App : Application
    {
        public IServiceProvider? ServiceProvider { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            var services = new ServiceCollection();
            var cfg = new ConfigurationBuilder()
                .AddJsonFile("knovara.config.json", optional:true, reloadOnChange:true)
                .Build();

            services.AddSingleton<IConfiguration>(cfg);
            services.AddLogging(builder => builder.AddConsole());
            services.AddTransport(cfg);

            ServiceProvider = services.BuildServiceProvider();

            var login = new LoginWindow();
            login.Show();
        }
    }
}
