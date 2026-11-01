using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Devices;

/// <summary>
/// Access to feedbackd, the feedback daemon of Linux mobile shells
/// (Phosh, postmarketOS, Mobian, PureOS...), on the session bus.
/// No UI toolkit dependency.
/// </summary>
internal static class Feedbackd
{
    public const string BusName = "org.sigxcpu.Feedback";
    public const string ObjectPath = "/org/sigxcpu/Feedback";

    /// <summary>Event based feedback: TriggerFeedback(s app_id, s event, a{sv} hints, i timeout) -> u.</summary>
    public const string FeedbackInterface = "org.sigxcpu.Feedback";

    /// <summary>Raw vibration patterns: Vibrate(s app_id, a(du) pattern) -> b. Not available in old feedbackd versions.</summary>
    public const string HapticInterface = "org.sigxcpu.Feedback.Haptic";

    static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(1);

    // Introspection XML of the feedbackd object, or null when feedbackd is not reachable.
    static readonly Lazy<string?> s_introspection = new(Introspect);

    /// <summary>
    /// Default application id sent to feedbackd (used for per-application settings).
    /// </summary>
    public static string DefaultAppId { get; } =
        Assembly.GetEntryAssembly()?.GetName().Name ?? "maui-app";

    /// <summary>
    /// Whether a vibration motor is present (see <see cref="HasVibraDevice"/>) and feedbackd
    /// is running or activatable and exposes <paramref name="interfaceName"/>.
    /// Evaluated once per process; the hardware is checked first, so desktops without a
    /// vibration motor never touch D-Bus, and the D-Bus probe is bounded.
    /// </summary>
    public static bool HasInterface(string interfaceName) => HasVibraDevice && HasInterfaceCore(interfaceName);

    static bool HasInterfaceCore(string interfaceName) =>
        s_introspection.Value?.Contains($"<interface name=\"{interfaceName}\"", StringComparison.Ordinal) == true;

    /// <summary>
    /// Sends a method call without blocking the caller; failures are only logged,
    /// like the other platforms (haptics never throw).
    /// </summary>
    /// <remarks>
    /// Uses the shared session connection, kept open for the lifetime of the process:
    /// feedbackd ends feedback started by a client when that client disconnects.
    /// </remarks>
    public static void Send<T>(Func<DBusConnection, MessageBuffer> createMessage, MessageValueReader<T> reader, Action<T>? onReply = null)
    {
        _ = SendAsync();

        async Task SendAsync()
        {
            try
            {
                var connection = DBusConnection.Session;
                var reply = await connection.CallMethodAsync(createMessage(connection), reader).ConfigureAwait(false);
                onReply?.Invoke(reply);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[feedbackd] call failed: {ex.Message}");
            }
        }
    }

    // ----- Vibration motor detection (/sys/class/input) -----

    const string InputClassPath = "/sys/class/input";
    const string UdevDataPath = "/run/udev/data";
    const string VibraUdevProperty = "E:FEEDBACKD_TYPE=vibra";

    // linux/input-event-codes.h
    const int FF_RUMBLE = 0x50;
    const int FF_PERIODIC = 0x51;

    // Driver names matched by feedbackd's 72-feedbackd.rules; only used when
    // the udev database can't be read (e.g. inside some sandboxes).
    static readonly string[] s_knownVibraNames =
    {
        "aw86224-haptics", "aw86927-haptics", "aw8695-haptics", "cs40l20",
        "gpio-vibrator", "pwm-vibrator", "regulator-haptic", "isa1200-haptic",
        "max77693-haptic", "twl4030:vibrator", "pm8xxx_vib_ffmemless",
        "spmi_haptics", "pmi8998_haptics", "drv260x:haptics", "drv2624:haptics",
    };

    static readonly Lazy<bool> s_hasVibraDevice = new(FindVibraDevice);

    /// <summary>
    /// Whether a vibration motor usable by feedbackd is present. Without it,
    /// feedbackd may run (sound/LED feedback) but can't vibrate, and a gamepad
    /// with rumble must not count as one.
    /// </summary>
    public static bool HasVibraDevice => s_hasVibraDevice.Value;

    /// <summary>
    /// Same criteria as feedbackd: an input event device tagged
    /// <c>FEEDBACKD_TYPE=vibra</c> by udev, supporting FF_RUMBLE and FF_PERIODIC.
    /// </summary>
    static bool FindVibraDevice()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(InputClassPath))
            return false;

        try
        {
            // Entries are symlinks to the device directories: enumerate entries, not directories.
            foreach (var eventDir in Directory.EnumerateFileSystemEntries(InputClassPath, "event*"))
            {
                var deviceDir = Path.Combine(eventDir, "device");

                if (!HasForceFeedback(Path.Combine(deviceDir, "capabilities", "ff")))
                    continue;

                if (IsTaggedVibra(eventDir) ?? IsKnownVibraName(deviceDir))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[feedbackd] vibra detection failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// <c>capabilities/ff</c> is a bitmap printed as hex words of the kernel's
    /// <c>long</c> size, most significant word first.
    /// </summary>
    static bool HasForceFeedback(string capabilitiesPath)
    {
        if (!File.Exists(capabilitiesPath))
            return false;

        var words = File.ReadAllText(capabilitiesPath)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var bitsPerWord = Environment.Is64BitOperatingSystem ? 64 : 32;

        return TestBit(words, FF_RUMBLE, bitsPerWord) && TestBit(words, FF_PERIODIC, bitsPerWord);
    }

    static bool TestBit(string[] words, int bit, int bitsPerWord)
    {
        var index = words.Length - 1 - bit / bitsPerWord;
        if (index < 0)
            return false;

        if (!ulong.TryParse(words[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var word))
            return false;

        return ((word >> (bit % bitsPerWord)) & 1) != 0;
    }

    /// <summary>
    /// Reads the udev database entry (<c>/run/udev/data/c&lt;major&gt;:&lt;minor&gt;</c>).
    /// Returns null when it isn't available.
    /// </summary>
    static bool? IsTaggedVibra(string eventDir)
    {
        var devFile = Path.Combine(eventDir, "dev");
        if (!File.Exists(devFile))
            return null;

        var udevFile = Path.Combine(UdevDataPath, "c" + File.ReadAllText(devFile).Trim());
        if (!File.Exists(udevFile))
            return null;

        try
        {
            return File.ReadLines(udevFile).Any(line => line.Trim() == VibraUdevProperty);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    static bool IsKnownVibraName(string deviceDir)
    {
        var nameFile = Path.Combine(deviceDir, "name");
        if (!File.Exists(nameFile))
            return false;

        var name = File.ReadAllText(nameFile).Trim();
        return s_knownVibraNames.Contains(name, StringComparer.Ordinal);
    }

    // ----- D-Bus -----

    static string? Introspect()
    {
        if (!OperatingSystem.IsLinux() || DBusAddress.Session is null)
            return null;

        try
        {
            // Task.Run: no SynchronizationContext, so waiting can't deadlock the UI thread.
            // Introspecting also D-Bus-activates feedbackd when it isn't running yet.
            var probe = Task.Run(() =>
            {
                var connection = DBusConnection.Session;
                return connection.CallMethodAsync(CreateIntrospectMessage(connection), ReadString);
            });

            return probe.Wait(s_probeTimeout) ? probe.Result : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[feedbackd] not available: {ex.Message}");
            return null;
        }
    }

    static MessageBuffer CreateIntrospectMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: BusName,
            path: ObjectPath,
            @interface: "org.freedesktop.DBus.Introspectable",
            member: "Introspect");
        return writer.CreateMessage();
    }

    static string ReadString(Message message, object? state) =>
        message.GetBodyReader().ReadString();
}