using LAWS.Voices.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LAWS.Voices.Forms
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory)
                          .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                })
                .ConfigureServices((context, services) =>
                {
                    var cfg = context.Configuration;
                    var appsettings = new LAWS.Voices.Shared.Appsettings();
                    cfg.Bind(appsettings);
                    services.AddSingleton(appsettings);

                    services.AddSingleton<WindowMain>();
                })
                .Build();

            var mainForm = host.Services.GetRequiredService<WindowMain>();

            StaticLogger.InitializeLogFiles(null, true, 16);
            StaticLogger.SetUiContext(SynchronizationContext.Current ?? new SynchronizationContext());

            Application.Run(mainForm);
        }
    }
}