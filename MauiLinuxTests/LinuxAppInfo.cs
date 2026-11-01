using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Platforms;

namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.AppModel;

public class LinuxAppInfo : IAppInfo
{
    private readonly Lazy<Assembly> _entry = new(() =>
		Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());

	public string PackageName => _entry.Value.GetName().Name ?? "unknown";
	public string Name => _entry.Value.GetCustomAttribute<AssemblyTitleAttribute>()?.Title
		?? _entry.Value.GetName().Name ?? "unknown";
	public string VersionString => _entry.Value.GetName().Version?.ToString() ?? "1.0.0";
	public Version Version => _entry.Value.GetName().Version ?? new Version(1, 0, 0);
	public string BuildString => _entry.Value.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
		?.InformationalVersion ?? VersionString;

	public AppTheme RequestedTheme => AppTheme.Unspecified;// GtkThemeManager.GetCurrentTheme();

	public AppPackagingModel PackagingModel => AppPackagingModel.Unpackaged;
	public LayoutDirection RequestedLayoutDirection => LayoutDirection.LeftToRight;

	public void ShowSettingsUI()
	{
		// ControlCenterCommand is a program, not a document: it must be started directly.
		// (xdg-open treats a bare name as a relative file path, e.g.
		// bin/Debug/net10.0/gnome-control-center, and fails.)
		var preferred = DesktopImplementation.Current.ControlCenterCommand;
		var candidates = new[] { preferred }.Concat(s_controlCenters).Distinct(StringComparer.Ordinal);

		foreach (var command in candidates)
		{
			var path = FindInPath(command);
			if (path is null)
				continue;

			try
			{
				using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
				return;
			}
			catch (Win32Exception ex)
			{
				Debug.WriteLine($"[LinuxAppInfo] {command} failed: {ex.Message}");
			}
		}

		throw new FeatureNotSupportedException(
			$"No settings application found for desktop '{DesktopImplementation.Current}' (tried: {string.Join(", ", candidates)}).");
	}

	// Known settings applications, tried when the desktop's own one is missing.
	static readonly string[] s_controlCenters =
	{
		"gnome-control-center", "systemsettings", "xfce4-settings-manager", "cinnamon-settings",
		"mate-control-center", "io.elementary.switchboard", "budgie-control-center", "lxqt-config",
		"dde-control-center", "cosmic-settings",
	};

	static string? FindInPath(string command)
	{
		if (Path.IsPathRooted(command))
			return File.Exists(command) ? command : null;

		return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(':', StringSplitOptions.RemoveEmptyEntries)
			.Select(dir => Path.Combine(dir, command))
			.FirstOrDefault(File.Exists);
	}
}