using System.Runtime.InteropServices;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Platforms;
using Tmds.DBus.Protocol;

namespace MauiLinuxTests.Pages;

/// <summary>
/// What the Linux implementations depend on: distribution, desktop, sandbox,
/// D-Bus buses, xdg-desktop-portal interfaces and command line tools.
/// </summary>
public class PlatformPage : TestPage
{
    static readonly string[] s_portals =
    {
        "org.freedesktop.portal.Email",
        "org.freedesktop.portal.OpenURI",
        "org.freedesktop.portal.Camera",
        "org.freedesktop.portal.Secret",
        "org.freedesktop.host.portal.Registry",
    };

    static readonly string[] s_tools =
    {
        "zenity", "kdialog", "xdg-email", "xdg-open", "gdbus", "kdeconnect-cli",
        "bluetooth-sendto", "bluedevil-sendfile", "gst-launch-1.0", "gst-inspect-1.0",
    };

    public PlatformPage() : base("Plateforme", null)
    {
        AddNote("Pré-requis des implémentations Linux : à lancer en premier pour savoir quoi attendre des autres pages.");

        AddButton("Système", () =>
        {
            Log($"OS : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            Log($"OperatingSystem.IsLinux() : {OperatingSystem.IsLinux()}");
            Log($".NET : {RuntimeInformation.FrameworkDescription}");

            if (!OperatingSystem.IsLinux())
                return;

            Log($"Distribution : {DistributionImplementation.Current}");
            Log($"Bureau : {DesktopImplementation.Current} (XDG_CURRENT_DESKTOP={Env("XDG_CURRENT_DESKTOP")})");
            Log($"Session : {Env("XDG_SESSION_TYPE")} (WAYLAND_DISPLAY={Env("WAYLAND_DISPLAY")}, DISPLAY={Env("DISPLAY")})");
            Log($"Bac à sable : {(File.Exists("/.flatpak-info") ? "Flatpak" : Env("SNAP") != "—" ? "Snap" : "aucun")}");
        });

        AddButton("Bus D-Bus et portails", async () =>
        {
            if (!OperatingSystem.IsLinux())
            {
                Log("Linux uniquement.");
                return;
            }

            Log($"Bus de session : {DBusAddress.Session ?? "absent"}");
            Log($"Bus système : {DBusAddress.System ?? "absent"}");

            if (!XdgDesktopPortal.IsSessionBusAvailable)
                return;

            foreach (var portal in s_portals)
            {
                try
                {
                    var version = await XdgDesktopPortal.GetPropertyAsync(portal, "version").WaitAsync(TimeSpan.FromSeconds(3));
                    Log($"{portal} : version {version.GetUInt32()}");
                }
                catch (Exception ex)
                {
                    Log($"{portal} : indisponible ({ex.GetType().Name})");
                }
            }
        });

        AddButton("Outils en ligne de commande", () =>
        {
            foreach (var tool in s_tools)
                Log($"{tool} : {FindInPath(tool) ?? "absent"}");
        });
    }

    static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : "—";

    static string? FindInPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, tool))
            .FirstOrDefault(File.Exists);
}
