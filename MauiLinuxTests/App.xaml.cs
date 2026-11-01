using Microsoft.Extensions.DependencyInjection;

namespace MauiLinuxTests
{
    public partial class App : Application
    {
        readonly IServiceProvider _services;

        public App(IServiceProvider services)
        {
            _services = services;
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            // Create the app actions service now (UI thread): on Linux it publishes again
            // the actions saved by the previous run, as Android/iOS keep their shortcuts.
            _ = _services.GetService<IAppActions>();

            return new Window(new AppShell(_services));
        }
    }
}