namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Platforms;

internal class DistributionImplementation
{
    private static readonly Lazy<Distribution> _current = new(Detect);

    public static Distribution Current => _current.Value;

    public static Distribution Detect()
    {
        if (OperatingSystem.IsLinux())
        {
            var osRelease = ReadOsRelease();
            var id = Get(osRelease, "ID");
            var idLike = Get(osRelease, "ID_LIKE");

            if (Matches(id, idLike, "ubuntu")) return Distribution.Ubuntu;
            if (Matches(id, idLike, "fedora")) return Distribution.Fedora;
            if (Matches(id, idLike, "arch")) return Distribution.ArchLinux;
            if (Matches(id, idLike, "debian")) return Distribution.Debian;
            if (Matches(id, idLike, "opensuse", "opensuse-leap", "opensuse-tumbleweed", "suse")) return Distribution.OpenSUSE;
            if (Matches(id, idLike, "linuxmint", "mint")) return Distribution.LinuxMint;
            if (Matches(id, idLike, "manjaro")) return Distribution.Manjaro;
            if (Matches(id, idLike, "pop", "pop_os")) return Distribution.PopOS;
            if (Matches(id, idLike, "centos")) return Distribution.CentOS;
            if (Matches(id, idLike, "rhel", "redhat")) return Distribution.RHEL;
            if (Matches(id, idLike, "rocky")) return Distribution.RockyLinux;
            if (Matches(id, idLike, "almalinux")) return Distribution.AlmaLinux;
            if (Matches(id, idLike, "gentoo")) return Distribution.Gentoo;
            if (Matches(id, idLike, "alpine")) return Distribution.Alpine;
            if (Matches(id, idLike, "slackware")) return Distribution.Slackware;
            if (Matches(id, idLike, "nixos")) return Distribution.NixOS;
            if (Matches(id, idLike, "kali")) return Distribution.KaliLinux;
            if (Matches(id, idLike, "elementary")) return Distribution.ElementaryOS;
        }

        return Distribution.Unknown;
    }

    private static Dictionary<string, string> ReadOsRelease()
    {
        const string path = "/etc/os-release";

        if (!File.Exists(path))
            return new Dictionary<string, string>();

        return File.ReadAllLines(path)
            .Select(ParseLine)
            .Where(x => x is not null)
            .ToDictionary(x => x!.Value.Key, x => x!.Value.Value);
    }

    private static KeyValuePair<string, string>? ParseLine(string line)
    {
        var separator = line.IndexOf('=');

        if (separator <= 0)
            return null;

        var key = line[..separator];
        var value = line[(separator + 1)..].Trim();

        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        return new(key, value);
    }

    private static string Get(
        Dictionary<string, string> values,
        string key)
    {
        return values.TryGetValue(key, out var value)
            ? value.ToLowerInvariant()
            : string.Empty;
    }

    private static bool Matches(
        string id,
        string idLike,
        params string[] values)
    {
        var identifiers = $"{id} {idLike}"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return values.Any(value =>
            identifiers.Contains(
                value,
                StringComparer.OrdinalIgnoreCase));
    }
}

public readonly struct Distribution : IEquatable<Distribution>
{
    readonly string distribution;

    public static Distribution Ubuntu { get; } = new Distribution(nameof(Ubuntu));
    public static Distribution Fedora { get; } = new Distribution(nameof(Fedora));
    public static Distribution ArchLinux { get; } = new Distribution(nameof(ArchLinux));
    public static Distribution Debian { get; } = new Distribution(nameof(Debian));
    public static Distribution OpenSUSE { get; } = new Distribution(nameof(OpenSUSE));
    public static Distribution LinuxMint { get; } = new Distribution(nameof(LinuxMint));
    public static Distribution Manjaro { get; } = new Distribution(nameof(Manjaro));
    public static Distribution PopOS { get; } = new Distribution(nameof(PopOS));
    public static Distribution CentOS { get; } = new Distribution(nameof(CentOS));
    public static Distribution RHEL { get; } = new Distribution(nameof(RHEL));
    public static Distribution RockyLinux { get; } = new Distribution(nameof(RockyLinux));
    public static Distribution AlmaLinux { get; } = new Distribution(nameof(AlmaLinux));
    public static Distribution Gentoo { get; } = new Distribution(nameof(Gentoo));
    public static Distribution Alpine { get; } = new Distribution(nameof(Alpine));
    public static Distribution Slackware { get; } = new Distribution(nameof(Slackware));
    public static Distribution NixOS { get; } = new Distribution(nameof(NixOS));
    public static Distribution KaliLinux { get; } = new Distribution(nameof(KaliLinux));
    public static Distribution ElementaryOS { get; } = new Distribution(nameof(ElementaryOS));

    public static Distribution Unknown { get; } = new Distribution(nameof(Unknown));

    internal Distribution(string distribution)
    {
        if (distribution == null)
            throw new ArgumentNullException(nameof(distribution));

        if (distribution.Length == 0)
            throw new ArgumentException(nameof(distribution));

        this.distribution = distribution;
    }

    /// <summary>
    /// Creates a new device platform instance. This can be used to define your custom platforms.
    /// </summary>
    /// <param name="devicePlatform">The device platform identifier.</param>
    /// <returns>A new instance of <see cref="DevicePlatform"/> with the specified platform identifier.</returns>
    public static Distribution Create(string distribution) =>
        new Distribution(distribution);

    /// <summary>
    /// Compares the underlying <see cref="DevicePlatform"/> instances.
    /// </summary>
    /// <param name="other"><see cref="DevicePlatform"/> object to compare with.</param>
    /// <returns><see langword="true"/> if they are equal, otherwise <see langword="false"/>.</returns>
    public bool Equals(Distribution other) =>
        Equals(other.distribution);

    internal bool Equals(string other) =>
        string.Equals(distribution, other, StringComparison.Ordinal);

    /// <inheritdoc cref="IEquatable{T}.Equals(T)"/>
    public override bool Equals(object? obj) =>
        obj is Distribution && Equals((Distribution)obj);

    /// <summary>
    /// Gets the hash code for this platform instance.
    /// </summary>
    /// <returns>The computed hash code for this device platform or <c>0</c> when the device platform is <see langword="null"/>.</returns>
    public override int GetHashCode() =>
        distribution == null ? 0 : distribution.GetHashCode(
            #if !NETSTANDARD2_0
                StringComparison.Ordinal
            #endif
            );

    /// <summary>
    /// Returns a string representation of the current value of <see cref="distribution"/>.
    /// </summary>
    /// <returns>A string representation of this instance in the format of <c>{device platform}</c> or an empty string when no device platform is set.</returns>
    public override string ToString() =>
        distribution ?? string.Empty;

    /// <summary>
    ///	Equality operator for equals.
    /// </summary>
    /// <param name="left">Left to compare.</param>
    /// <param name="right">Right to compare.</param>
    /// <returns><see langword="true"/> if objects are equal, otherwise <see langword="false"/>.</returns>
    public static bool operator ==(Distribution left, Distribution right) =>
        left.Equals(right);

    /// <summary>
    /// Inequality operator.
    /// </summary>
    /// <param name="left">Left to compare.</param>
    /// <param name="right">Right to compare.</param>
    /// <returns><see langword="true"/> if objects are not equal, otherwise <see langword="false"/>.</returns>
    public static bool operator !=(Distribution left, Distribution right) =>
        !left.Equals(right);
}