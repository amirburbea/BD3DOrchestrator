using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BD3DOrchestrator;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        IHost host;
        try
        {
            host = BuildHost(args);
            host.Start(); // runs the options validation (ValidateOnStart); there are no hosted services
        }
        catch (Exception ex)
        {
            MessageBox.Show(Describe(ex), "BD3D Orchestrator: cannot start", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        using (host)
        {
            App app = host.Services.GetRequiredService<App>();
            app.InitializeComponent();
            app.Run();
        }
    }

    private static IHost BuildHost(string[] args)
    {
        // Content root = the exe folder, so config files are found no matter where the app is launched from.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        // Settings come only from these sources (later wins), so a typo'd or stale key is an error instead of being ignored:
        //   appsettings.json        shared, identical on every PC
        //   appsettings.local.json  optional, this PC only (e.g. the Beelink's D:/C: project/output split)
        //   BD3D_* env variables    e.g. BD3D_ProjectDirectory
        IConfigurationRoot settingsConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddEnvironmentVariables("BD3D_")
            .Build();
        builder.Services
            .AddOptions<AppSettings>()
            .Bind(settingsConfig, binder => binder.ErrorOnUnknownConfiguration = true)
            .PostConfigure(settings => settings.ExpandEnvironmentVariables())
            .ValidateOnStart();
        builder.Services
            .AddOptions<TmdbOptions>()
            .Bind(settingsConfig.GetSection("Tmdb"), binder => binder.ErrorOnUnknownConfiguration = true);
        // MovieDetector is captured by the singleton JobRunner, so its HttpClient lives for the whole session. The factory's
        // handler rotation can't help a client that is never released, so it is switched off (infinite handler lifetime) and
        // the handler itself retires pooled connections instead, which keeps DNS fresh.
        builder.Services
            .AddHttpClient<MovieDetector>(client => client.Timeout = TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        builder.Services
            .AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>()
            .AddSingleton(sp => sp.GetRequiredService<IOptions<AppSettings>>().Value)
            .AddSingleton<MainWindowViewModel>()
            .AddSingleton<IMessageLogger>(sp => sp.GetRequiredService<MainWindowViewModel>())
            .AddSingleton<MediaInspector>()
            .AddSingleton<OrchestratorPipeline>()
            .AddSingleton<JobRunner>()
            .AddSingleton<App>()
            .AddSingleton<MainWindow>()
            .AddSingleton<Func<MainWindow>>(sp => () => sp.GetRequiredService<MainWindow>());
        return builder.Build();
    }

    private static string Describe(Exception ex) => ex switch
    {
        OptionsValidationException validation => "Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, validation.Failures.Select(f => "  • " + f)),
        AggregateException aggregate => string.Join(Environment.NewLine + Environment.NewLine, aggregate.Flatten().InnerExceptions.Select(Describe)),
        _ => ex.GetBaseException().Message
    };
}
