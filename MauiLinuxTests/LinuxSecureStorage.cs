using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Storage;

/// <summary>
/// <see cref="ISecureStorage"/> backed by the xdg-desktop-portal Secret portal.
/// </summary>
/// <remarks>
/// <para>
/// The portal (<c>org.freedesktop.portal.Secret.RetrieveSecret</c>) hands out a master
/// secret unique to the application, kept by the user's keyring (GNOME Keyring / oo7 on
/// GNOME, KWallet on KDE). Values are encrypted with AES-256-GCM using a key derived from
/// that secret (HKDF-SHA256) and stored in <c>$XDG_DATA_HOME/&lt;app id&gt;/securestorage.json</c>
/// (inside the sandbox data directory for Flatpak apps).
/// </para>
/// <para>
/// Outside a sandbox, the app registers its application id with
/// <c>org.freedesktop.host.portal.Registry</c> (xdg-desktop-portal 1.19+); the id must
/// match the basename of the app's .desktop file.
/// </para>
/// No UI toolkit dependency: works for both the Gtk4 and the Avalonia backends.
/// </remarks>
public partial class LinuxSecureStorage : ISecureStorage, IDisposable
{
    const string SecretInterface = "org.freedesktop.portal.Secret";
    const string RegistryInterface = "org.freedesktop.host.portal.Registry";

    const int FormatVersion = 1;
    const int KeySize = 32;     // AES-256
    const int NonceSize = 12;   // AES-GCM
    const int TagSize = 16;
    static readonly byte[] s_keyInfo = Encoding.UTF8.GetBytes("Microsoft.Maui.SecureStorage.v1");

    // The keyring may have to be unlocked by the user.
    static readonly TimeSpan s_secretTimeout = TimeSpan.FromMinutes(2);

    readonly string _appId;
    readonly string _storePath;
    readonly SemaphoreSlim _gate = new(1, 1);
    byte[]? _key;
    bool _disposed;

    /// <param name="appId">
    /// Application id. Outside a sandbox it is registered with the portal and must match the
    /// basename of the app's .desktop file (e.g. "com.companyname.myapp").
    /// Defaults to the entry assembly name.
    /// </param>
    public LinuxSecureStorage(string? appId = null)
    {
        _appId = string.IsNullOrWhiteSpace(appId)
            ? Assembly.GetEntryAssembly()?.GetName().Name ?? "maui-app"
            : appId;

        _storePath = Path.Combine(GetDataHome(), _appId, "securestorage.json");
    }

    /// <summary>
    /// Describes where secrets come from.
    /// </summary>
    public string Backend => "xdg-desktop-portal (org.freedesktop.portal.Secret)";

    static bool IsSandboxed =>
        File.Exists("/.flatpak-info") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP"));

    // ----- ISecureStorage -----

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var store = LoadStore();
            if (!store.Items.TryGetValue(key, out var encrypted))
                return null; // no need to touch the keyring

            var cryptoKey = await GetKeyAsync(store).ConfigureAwait(false);
            if (TryDecrypt(cryptoKey, key, encrypted, out var value))
                return value;

            // Like the other platforms: a value that can't be decrypted anymore
            // (e.g. the keyring was reset) is removed and reported as missing.
            store.Items.Remove(key);
            SaveStore(store);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ThrowIfDisposed();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var store = LoadStore();
            var cryptoKey = await GetKeyAsync(store).ConfigureAwait(false);

            store.Items[key] = Encrypt(cryptoKey, key, value);
            SaveStore(store);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();

        // Removing doesn't need the secret.
        _gate.Wait();
        try
        {
            var store = LoadStore();
            if (!store.Items.Remove(key))
                return false;

            SaveStore(store);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void RemoveAll()
    {
        ThrowIfDisposed();

        _gate.Wait();
        try
        {
            var store = LoadStore();
            if (store.Items.Count == 0)
                return;

            store.Items.Clear();
            SaveStore(store);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_key is not null)
            CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // ----- Encryption -----

    static string Encrypt(byte[] cryptoKey, string name, string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        var payload = new byte[NonceSize + plaintext.Length + TagSize];

        var nonce = payload.AsSpan(0, NonceSize);
        var ciphertext = payload.AsSpan(NonceSize, plaintext.Length);
        var tag = payload.AsSpan(NonceSize + plaintext.Length, TagSize);

        RandomNumberGenerator.Fill(nonce);

        // The entry name is authenticated: a value can't be moved to another key.
        using var aes = new AesGcm(cryptoKey, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(name));

        CryptographicOperations.ZeroMemory(plaintext);
        return Convert.ToBase64String(payload);
    }

    static bool TryDecrypt(byte[] cryptoKey, string name, string encrypted, out string? value)
    {
        value = null;
        try
        {
            var payload = Convert.FromBase64String(encrypted);
            if (payload.Length < NonceSize + TagSize)
                return false;

            var nonce = payload.AsSpan(0, NonceSize);
            var ciphertext = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
            var tag = payload.AsSpan(payload.Length - TagSize, TagSize);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(cryptoKey, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(name));

            value = Encoding.UTF8.GetString(plaintext);
            CryptographicOperations.ZeroMemory(plaintext);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// The AES key: HKDF-SHA256 of the portal master secret, salted per store.
    /// Retrieved once per instance.
    /// </summary>
    async Task<byte[]> GetKeyAsync(Store store)
    {
        if (_key is not null)
            return _key;

        if (store.Salt is null)
        {
            store.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            SaveStore(store);
        }

        var (secret, token) = await RetrieveMasterSecretAsync(store.Token).ConfigureAwait(false);
        try
        {
            // The portal may hand out an identifier to pass back on the next call.
            if (token is not null && token != store.Token)
            {
                store.Token = token;
                SaveStore(store);
            }

            _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize, Convert.FromBase64String(store.Salt), s_keyInfo);
            return _key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    // ----- Secret portal -----

    /// <summary>
    /// RetrieveSecret(h fd, a{sv} options) -> o handle: the backend writes the
    /// master secret into the given pipe, and may return a "token" in the Response results.
    /// </summary>
    async Task<(byte[] Secret, string? Token)> RetrieveMasterSecretAsync(string? previousToken)
    {
        if (!XdgDesktopPortal.IsSessionBusAvailable)
            throw new FeatureNotSupportedException("SecureStorage requires a D-Bus session bus and xdg-desktop-portal.");

        // Dedicated connection: host app registration must be the first portal call
        // made by a connection, and the shared session connection may already have used portals.
        using var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync().ConfigureAwait(false);

        if (!IsSandboxed)
            await RegisterHostAppAsync(connection).ConfigureAwait(false);

        var (readEnd, writeEnd) = CreatePipe();
        using var reader = new FileStream(readEnd, FileAccess.Read, bufferSize: 1);

        // Read while the backend writes; EOF once every write end is closed
        // (ours is closed by Tmds.DBus once the call is sent).
        var readTask = Task.Run(() =>
        {
            using var buffer = new MemoryStream();
            reader.CopyTo(buffer);
            return buffer.ToArray();
        });

        PortalResponse? response;
        try
        {
            var handleToken = XdgDesktopPortal.NewHandleToken();
            response = await XdgDesktopPortal.RequestAsync(
                connection,
                handleToken,
                c => CreateRetrieveSecretMessage(c, writeEnd, handleToken, previousToken),
                s_secretTimeout).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex) when (XdgDesktopPortal.IsPortalMissing(ex))
        {
            writeEnd.Dispose();
            throw new FeatureNotSupportedException("The xdg-desktop-portal Secret portal is not available (install a backend such as gnome-keyring, oo7 or KWallet).", ex);
        }
        catch
        {
            writeEnd.Dispose();
            throw;
        }

        if (response is null)
            throw new TimeoutException("The Secret portal did not answer.");

        if (response.Code != XdgDesktopPortal.ResponseSuccess)
            throw new InvalidOperationException(response.Code == XdgDesktopPortal.ResponseCancelled
                ? "Access to the keyring was cancelled."
                : $"The Secret portal refused to provide a secret for '{_appId}'.");

        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        if (completed != readTask)
            throw new TimeoutException("The Secret portal did not deliver the secret.");

        var secret = await readTask.ConfigureAwait(false);
        if (secret.Length == 0)
            throw new InvalidOperationException("The Secret portal returned an empty secret.");

        var token = response.Results.TryGetValue("token", out var t) && t.Type == VariantValueType.String
            ? t.GetString()
            : null;

        return (secret, token);
    }

    // Register(s app_id, a{sv} options)
    async Task RegisterHostAppAsync(DBusConnection connection)
    {
        try
        {
            await connection.CallMethodAsync(CreateRegisterMessage(connection, _appId)).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex) when (XdgDesktopPortal.IsPortalMissing(ex))
        {
            // xdg-desktop-portal < 1.19: no registry. The backend may then refuse an unidentified app.
        }
    }

    static MessageBuffer CreateRegisterMessage(DBusConnection connection, string appId)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: XdgDesktopPortal.BusName,
            path: XdgDesktopPortal.ObjectPath,
            @interface: RegistryInterface,
            member: "Register",
            signature: "sa{sv}");

        writer.WriteString(appId);
        var options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(options);

        return writer.CreateMessage();
    }

    static MessageBuffer CreateRetrieveSecretMessage(DBusConnection connection, SafeFileHandle fd, string handleToken, string? token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: XdgDesktopPortal.BusName,
            path: XdgDesktopPortal.ObjectPath,
            @interface: SecretInterface,
            member: "RetrieveSecret",
            signature: "ha{sv}");

        writer.WriteHandle(fd); // owned by the message from here

        var options = writer.WriteDictionaryStart();

        writer.WriteDictionaryEntryStart();
        writer.WriteString("handle_token");
        writer.WriteVariantString(handleToken);

        if (!string.IsNullOrEmpty(token))
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("token");
            writer.WriteVariantString(token);
        }

        writer.WriteDictionaryEnd(options);

        return writer.CreateMessage();
    }

    static (SafeFileHandle ReadEnd, SafeFileHandle WriteEnd) CreatePipe()
    {
        Span<int> fds = stackalloc int[2];
        if (pipe2(ref MemoryMarshal.GetReference(fds), O_CLOEXEC) != 0)
            throw new IOException($"pipe2 failed (errno {Marshal.GetLastPInvokeError()}).");

        return (new SafeFileHandle(fds[0], ownsHandle: true), new SafeFileHandle(fds[1], ownsHandle: true));
    }

    const int O_CLOEXEC = 0x80000;

    [DllImport("libc", SetLastError = true)]
    static extern int pipe2(ref int fds, int flags);

    // ----- Store file -----

    sealed class Store
    {
        [JsonPropertyName("version")] public int Version { get; set; } = FormatVersion;
        [JsonPropertyName("salt")] public string? Salt { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("items")] public Dictionary<string, string> Items { get; set; } = new(StringComparer.Ordinal);
    }

    Store LoadStore()
    {
        if (!File.Exists(_storePath))
            return new Store();

        try
        {
            var store = JsonSerializer.Deserialize(File.ReadAllText(_storePath), StoreJsonContext.Default.Store);
            if (store is not null && store.Version == FormatVersion)
            {
                store.Items ??= new Dictionary<string, string>(StringComparer.Ordinal);
                return store;
            }
        }
        catch (JsonException)
        {
        }

        // Unreadable or unknown format: start over rather than fail forever.
        return new Store();
    }

    void SaveStore(Store store)
    {
        var directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);

        // Created owner-only (0600) from the start, then renamed so a crash never leaves a truncated store.
        var temp = _storePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        File.Delete(temp); // UnixCreateMode only applies to a newly created file
        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, store, StoreJsonContext.Default.Store);
        }

        File.Move(temp, _storePath, overwrite: true);
    }

    static string GetDataHome()
    {
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathRooted(xdgDataHome))
            return xdgDataHome;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    // Source-generated: trimming / NativeAOT friendly.
    [JsonSerializable(typeof(Store))]
    sealed partial class StoreJsonContext : JsonSerializerContext
    {
    }
}