using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommandLiners;
using CommandLiners.Options;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Builder.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mono.Options;
using Rnwood.Smtp4dev.Controllers;
using Rnwood.Smtp4dev.DbModel;
using Rnwood.Smtp4dev.Server;
using Rnwood.Smtp4dev.Server.Settings;
using Rnwood.Smtp4dev.Service;
using Serilog;

namespace Rnwood.Smtp4dev
{
    public class Program
    {
        public static bool IsService { get; private set; }

        private static ILogger _log;

        public static async Task Main(string[] args)
        {

            try
            {
                var host = await StartApp(args, false, null);

                if (host == null)
                {
                    Environment.Exit(1);
                }
                else
                {
                    await host.WaitForShutdownAsync();
                }
                Log.Information("Exiting");
            }
            catch (CommandLineOptionsException ex)
            {
                if (ex.IsHelpRequest)
                {
                    Log.Information(ex.Message);
                }
                else
                {
                    Log.Fatal(ex.Message);
                    Environment.Exit(1);
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "A unhandled exception occurred.");
                Environment.Exit(1);
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        public static async Task<IHost> StartApp(IEnumerable<string> args, bool isDesktopApp, Action<CommandLineOptions> fixedOptions)
        {
            SetupStaticLogger(args);
            _log = Log.ForContext<Program>();

            string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            _log.Information("smtp4dev version {version}", version);
            _log.Information("https://github.com/rnwood/smtp4dev");
            _log.Information(".NET Core runtime version: {netcoreruntime}", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);


            if (!Debugger.IsAttached && args.Contains("--service"))
                IsService = true;

            MapOptions<CommandLineOptions> commandLineOptions = CommandLineParser.TryParseCommandLine(args, isDesktopApp);

            CommandLineOptions cmdLineOptions = new CommandLineOptions();
            new ConfigurationBuilder().AddCommandLineOptions(commandLineOptions).Build().Bind(cmdLineOptions);
            fixedOptions?.Invoke(cmdLineOptions);

            if (!string.IsNullOrEmpty(cmdLineOptions.InstallPath))
            {
                Directory.SetCurrentDirectory(cmdLineOptions.InstallPath);
            }

            _log.Information("Install location: {installpath}", Directory.GetCurrentDirectory());

            var host = BuildWebHost(args.Where(arg => arg != "--service").ToArray(), cmdLineOptions, commandLineOptions);



            await host.StartAsync();


            var addressesFeature = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var urls = addressesFeature.Addresses;

            foreach (var url in urls)
            {
                _log.Information("Now listening on: {url}", url);
            }

            return host;


        }


        private static string GetContentRoot()
        {
            string installLocation = AppContext.BaseDirectory;

            if (Directory.Exists(Path.Join(installLocation, "wwwroot")))
            {
                return installLocation;
            }

            string cwd = Directory.GetCurrentDirectory();
            if (Directory.Exists(Path.Join(cwd, "wwwroot")))
            {
                return cwd;
            }

            throw new ApplicationException($"Unable to find wwwroot in either '{installLocation}' or the CWD '{cwd}'");
        }

        private static IHost BuildWebHost(string[] args, CommandLineOptions cmdLineOptions, MapOptions<CommandLineOptions> commandLineOptions)
        {
            var contentRoot = GetContentRoot();
            var dataDir = GetOrCreateDataDir(cmdLineOptions);
            _log.Information("DataDir: {dataDir}", dataDir);
            Directory.SetCurrentDirectory(dataDir);

            var cb = new ConfigurationBuilder()
                            .SetBasePath(contentRoot)
                            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            _log.Information("Default settings file: {file}", Path.Join(contentRoot, "appsettings.json"));

            if (!cmdLineOptions.NoUserSettings)
            {
                cb = cb.AddJsonFile(Path.Join(dataDir, "appsettings.json"), optional: true, reloadOnChange: true);

                _log.Information("User settings file: {file}", Path.Join(dataDir, "appsettings.json"));
            }

            cb.AddEnvironmentVariables()
                .AddCommandLineOptions(commandLineOptions);
            var config = cb.Build();

            IHostBuilder builder = Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .UseContentRoot(contentRoot)
                .ConfigureAppConfiguration(
                    (hostingContext, configBuilder) =>
                    {
                        var env = hostingContext.HostingEnvironment;

                        configBuilder.Sources.Clear();
                        foreach(var source in cb.Sources)
                        {
                            configBuilder.Sources.Add(source);
                        }
                        hostingContext.HostingEnvironment.EnvironmentName = config["Environment"];

                        if (cmdLineOptions.DebugSettings)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(new SettingsDebugInfo
                            {
                                CmdLineArgs = Environment.GetCommandLineArgs(),
                                CmdLineOptions = cmdLineOptions,
                                Sources = cb.Sources.Select( s=>
                                {
                                    var source = new ConfigurationBuilder().Add(s).Build();

                                    return new SettingsSourceDebugInfo
                                    {
                                        Description = s is JsonConfigurationSource js ? js.FileProvider.GetFileInfo(js.Path).PhysicalPath : s.ToString(),
                                        ServerOptions = source.GetSection("ServerOptions").Get<ServerOptionsSource>(),
                                        RelayOption = source.GetSection("RelayOptions").Get<RelayOptionsSource>(),
                                        DesktopOptions = source.GetSection("DesktopOptions").Get<DesktopOptionsSource>()
                                    };
                                }).ToArray(),
                                Result = new SettingsResultDebugInfo
                                {
                                    ServerOptions = config.GetSection("ServerOptions").Get<ServerOptions>(),
                                    RelayOption = config.GetSection("RelayOptions").Get<RelayOptions>(),
                                    DesktopOptions = config.GetSection("DesktopOptions").Get<DesktopOptions>()
                                }
                            }, SettingsDebugInfoSerializationContext.Default.SettingsDebugInfo));
                        }


                    });


            builder.ConfigureWebHostDefaults((c) =>
            {
                c.UseStartup<Startup>();
                c.UseShutdownTimeout(TimeSpan.FromSeconds(10));

                
                ServerOptions serverOptions = config.GetSection("ServerOptions").Get<ServerOptions>();

                if (!string.IsNullOrEmpty(cmdLineOptions.Urls))
                {
                    c.UseUrls(cmdLineOptions.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()).ToArray());

                }
                else if (!string.IsNullOrEmpty(serverOptions.Urls))
                {
                    c.UseUrls(serverOptions.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()).ToArray());
                }

                c.ConfigureServices((webBuilderContext, services) =>
                {
                    services.AddSingleton(cmdLineOptions);
                    services.AddSingleton(commandLineOptions);
                    services.AddHostedService(sp => (Smtp4devServer)sp.GetRequiredService<ISmtp4devServer>());
                    services.AddHostedService(sp => sp.GetRequiredService<ImapServer>());
                });
            });

            builder.UseWindowsService(s => s.ServiceName = "smtp4dev");
        



            return builder.Build();
        }

        private static string GetOrCreateDataDir(CommandLineOptions cmdLineOptions)
        {
            var dataDir = DirectoryHelper.GetDataDir(cmdLineOptions);
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }
            return dataDir;
        }

        public static void SetupStaticLogger(IEnumerable<string> args)
        {
            try
            {
                IConfigurationRoot configuration =
                   new ConfigurationBuilder()
                       .AddJsonFile("appsettings.json")
                       .Build();

                var logConfigBuilder = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration);
                if (args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase)))
                {
#pragma warning disable CA1416 // Validate platform compatibility
                    logConfigBuilder.WriteTo.EventLog("smtp4dev");
#pragma warning restore CA1416 // Validate platform compatibility
                }
                Log.Logger = logConfigBuilder
                    .CreateLogger();
            }
            catch
            {
                //Ensure output goes somewhere if there's a config error.
                var logConfigBuilder = new LoggerConfiguration();
                if (args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase)))
                {
#pragma warning disable CA1416 // Validate platform compatibility
                    logConfigBuilder.WriteTo.EventLog("smtp4dev");
#pragma warning restore CA1416 // Validate platform compatibility
                }
                else
                {
                    logConfigBuilder.WriteTo.Console();
                }
                Log.Logger = logConfigBuilder.CreateLogger();
                throw;
            }


        }
    }

    internal class SettingsDebugInfo
    {
        public string[] CmdLineArgs { get; set; }
        public CommandLineOptions CmdLineOptions { get; set; }

        public SettingsSourceDebugInfo[] Sources { get; set; }
        public SettingsResultDebugInfo Result { get; set; }
    }

    internal class SettingsSourceDebugInfo
    {
        public string Description { get; set; }
        public ServerOptionsSource ServerOptions { get; set; }
        public RelayOptionsSource RelayOption { get; set; }
        public DesktopOptionsSource DesktopOptions { get; set; }
    }

    internal class SettingsResultDebugInfo
    {
        public ServerOptions ServerOptions { get; set; }
        public RelayOptions RelayOption { get; set; }
        public DesktopOptions DesktopOptions { get; set; }
    }
    

        [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(SettingsDebugInfo))]
    internal partial class SettingsDebugInfoSerializationContext : JsonSerializerContext {

    }
}
