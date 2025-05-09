﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Reflection;
using System.Threading.Tasks.Schedulers;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using System.Reactive;
using Microsoft.Extensions.Options;
using Rnwood.Smtp4dev.Server.Settings;

namespace Rnwood.Smtp4dev.Desktop
{
    public class Program
    {



        [STAThread]
        static async Task Main(string[] args)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                DesktopApp.HideConsole();
                StaTaskScheduler scheduler = new StaTaskScheduler(64);
                await Task.Factory.StartNew(() => RunAsync(args), CancellationToken.None, TaskCreationOptions.None, scheduler);
            }
            else
            {
                await RunAsync(args);
            }
        }

        private static async Task RunAsync(string[] args)
        {
            Rnwood.Smtp4dev.Program.SetupStaticLogger(args);
            string origWorkingDir = AppContext.BaseDirectory;
            DesktopApp app = null;

            try
            {
                app = new DesktopApp();

                var host =
                       await Rnwood.Smtp4dev.Program.StartApp(args, true, o =>
                       { 
                           o.Urls = "http://127.0.0.1:0";
                           o.IsDesktopApp = true;

                       });




                var addressesFeature = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                var urls = addressesFeature.Addresses;
                var appUrl = new Uri(urls.First());

                var desktopOptions = host.Services.GetService<IOptionsMonitor<DesktopOptions>>();

                app.Run(origWorkingDir, appUrl, desktopOptions);

                await host.StopAsync();


            }
            catch (CommandLineOptionsException ex)
            {
                DesktopApp.ShowFatalError(origWorkingDir, "Command Line " + (ex.IsHelpRequest ? "Help" : "Error"), ex.Message);
                return;
            }
            catch (Exception ex)
            {
                DesktopApp.ShowFatalError(origWorkingDir, "Fatal Error", ex.Message);
            }
            finally
            {

                app?.Exit();
            }
        }

    }



}
