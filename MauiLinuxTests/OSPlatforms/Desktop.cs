namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Platforms;

internal class DesktopImplementation
{
    private static readonly Lazy<Desktop> _current = new(Detect);

    public static Desktop Current => _current.Value;

    private static Desktop Detect()
    {
        if (OperatingSystem.IsLinux())
        {
            var xdgDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
            var desktopSession = Environment.GetEnvironmentVariable("DESKTOP_SESSION") ?? "";
            var combined = $"{xdgDesktop} {desktopSession}".ToUpperInvariant();

            if (combined.Contains("GNOME")) return Desktop.Gnome;
            if (combined.Contains("KDE")) return Desktop.KDE;
            if (combined.Contains("XFCE")) return Desktop.Xfce;
            if (combined.Contains("MATE")) return Desktop.Mate;
            if (combined.Contains("CINNAMON")) return Desktop.Cinnamon;
            if (combined.Contains("UNITY")) return Desktop.Unity;
            if (combined.Contains("LXDE")) return Desktop.LXDE;
            if (combined.Contains("LXQT")) return Desktop.LXQt;
            if (combined.Contains("PANTHEON")) return Desktop.Pantheon;
            if (combined.Contains("DEEPIN")) return Desktop.Deepin;
            if (combined.Contains("BUDGIE")) return Desktop.Budgie;
            if (combined.Contains("ENLIGHTENMENT")) return Desktop.Enlightenment;
            if (IsWsl()) return Desktop.WSL;
        }
        return Desktop.Unknown;
    }

    private static bool IsWsl()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP")))
        {
            return true;
        }

        try
        {
            if (File.Exists("/proc/sys/kernel/osrelease"))
            {
                var versionText = File.ReadAllText("/proc/sys/kernel/osrelease");
                if (versionText.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ||
                    versionText.Contains("wsl", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore I/O or permission errors
        }

        return false;
    }
}

public readonly struct Desktop : IEquatable<Desktop>
{
    readonly string desktop;

    public static Desktop Gnome { get; } = new Desktop(nameof(Gnome));
    public static Desktop Unknown { get; } = new Desktop(nameof(Unknown));
    public static Desktop KDE { get; } = new Desktop(nameof(KDE));
    public static Desktop Xfce { get; } = new Desktop(nameof(Xfce));
    public static Desktop Mate { get; } = new Desktop(nameof(Mate));
    public static Desktop Cinnamon { get; } = new Desktop(nameof(Cinnamon));
    public static Desktop WSL { get; } = new Desktop(nameof(WSL));
    public static Desktop Unity { get; } = new Desktop(nameof(Unity));
    public static Desktop LXDE { get; } = new Desktop(nameof(LXDE));
    public static Desktop LXQt { get; } = new Desktop(nameof(LXQt));
    public static Desktop Pantheon { get; } = new Desktop(nameof(Pantheon));
    public static Desktop Deepin { get; } = new Desktop(nameof(Deepin));
    public static Desktop Budgie { get; } = new Desktop(nameof(Budgie));
    public static Desktop Enlightenment { get; } = new Desktop(nameof(Enlightenment));

    internal Desktop(string desktop)
    {
        if (desktop == null)
            throw new ArgumentNullException(nameof(desktop));

        if (desktop.Length == 0)
            throw new ArgumentException(nameof(desktop));

        this.desktop = desktop;
    }

    public string ControlCenterCommand => this switch
    {
        var d when d == Gnome || d == Budgie => "gnome-control-center",
        var d when d == KDE => "systemsettings",
        var d when d == Xfce => "xfce4-settings-manager",
        var d when d == Cinnamon => "cinnamon-settings",
        var d when d == Pantheon => "io.elementary.switchboard",
        var d when d == Deepin => "dde-control-center",
        var d when d == LXQt => "lxqt-config",
        var d when d == Mate => "mate-control-center",
        var d when d == LXDE => "lxsession-edit",
        var d when d == Enlightenment => "enlightenment_config",
        _ => "gnome-control-center"
    };

    public static Desktop Create(string distribution) =>
        new Desktop(distribution);

    public bool Equals(Desktop other) =>
        Equals(other.desktop);

    internal bool Equals(string other) =>
        string.Equals(desktop, other, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is Desktop && Equals((Desktop)obj);

    public override int GetHashCode() =>
        desktop == null ? 0 : desktop.GetHashCode(
#if !NETSTANDARD2_0
            StringComparison.Ordinal
#endif
        );

    public override string ToString() =>
        desktop ?? string.Empty;

    public static bool operator ==(Desktop left, Desktop right) =>
        left.Equals(right);

    public static bool operator !=(Desktop left, Desktop right) =>
        !left.Equals(right);
}