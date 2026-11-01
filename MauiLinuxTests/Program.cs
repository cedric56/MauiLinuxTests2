using Microsoft.Maui.Platforms.Linux.Gtk4.Platform;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;

namespace MauiLinuxTests
{
    [SupportedOSPlatform("linux")]
    public class Program : GtkMauiApplication
    {
        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        public static void Main(string[] args)
        {
            var app = new Program();
            app.Run(args);
        }
    }
}
