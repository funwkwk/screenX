using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ScreenX;

public enum LicenceState { None, Active, Revoked, Invalid }

public sealed record DeviceInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("lastSeen")] string? LastSeen);

/// <summary>Outcome of a call to the licence server. Offline / server trouble is <see cref="Offline"/> and never changes the stored state.</summary>
public sealed record ApiResult(int Status, string? Code, string? Error, JsonElement Body, bool Offline)
{
    public bool Ok => Status is >= 200 and < 300;
    public List<DeviceInfo> Devices()
    {
        try
        {
            if (Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("devices", out var d))
                return d.Deserialize<List<DeviceInfo>>() ?? new();
        }
        catch { }
        return new();
    }
}

/// <summary>
/// Licence storage and the activate / deactivate calls from docs/UPDATE-API.md (screenX website repo).
/// The licence string and device id live in %AppData%\screenX\licence.json, the licence encrypted with Windows DPAPI
/// (current user). The device id is a SHA-256 of the machine GUID (never sent raw) or a random id if that is unavailable.
/// Nothing secret is compiled into the app.
/// </summary>
public static class Licence
{
    /// <summary>The one place the server address is set. SCREENX_API_URL overrides it (local testing against `vercel dev`).</summary>
    public const string ApiBaseUrl = "https://screenx.funwkwk.my.id";
    public const string OsName = "windows";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly object Gate = new();

    sealed class Stored
    {
        public string? LicenceProtected { get; set; }
        public string? DeviceId { get; set; }
        public string State { get; set; } = nameof(LicenceState.None);
        public string? LastCheck { get; set; }
    }

    static Stored data = LoadFile();

    /// <summary>SCREENX_DATA_DIR redirects the licence file (used by the licence self-test so it never touches real data).</summary>
    static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("SCREENX_DATA_DIR") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "screenX"), "licence.json");

    static string BaseUrl => (Environment.GetEnvironmentVariable("SCREENX_API_URL") is { Length: > 0 } u ? u : ApiBaseUrl).TrimEnd('/');

    static Stored LoadFile()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    static void SaveFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static event Action? Changed;

    // ---------------------------------------------------------------- state

    public static LicenceState State =>
        Enum.TryParse<LicenceState>(data.State, out var s) ? s : LicenceState.None;

    /// <summary>True while the stored licence was accepted by the server and not revoked. Stays true offline.
    /// Gate Pro features on this.</summary>
    public static bool IsPro => State == LicenceState.Active && Key != null;

    /// <summary>The pasted licence string, or null if none is stored.</summary>
    public static string? Key
    {
        get
        {
            if (string.IsNullOrEmpty(data.LicenceProtected)) return null;
            try
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(data.LicenceProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch { return null; }
        }
    }

    static void StoreKey(string? key)
    {
        data.LicenceProtected = string.IsNullOrEmpty(key) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    }

    static void SetState(LicenceState s)
    {
        lock (Gate) { if (State == s) return; data.State = s.ToString(); SaveFile(); }
        Changed?.Invoke();
    }

    public static string StatusText => State switch
    {
        LicenceState.Active => "screenX Pro is active on this PC",
        LicenceState.Revoked => "This licence was revoked (refunded). Pro features and updates are off",
        LicenceState.Invalid => "This licence is not valid. Enter it again",
        _ => "No licence entered",
    };

    // ---------------------------------------------------------------- device id

    /// <summary>Stable per-install id: SHA-256 of the Windows MachineGuid (hex, 32 chars); random GUID fallback. Created once.</summary>
    public static string DeviceId
    {
        get
        {
            lock (Gate)
            {
                if (string.IsNullOrEmpty(data.DeviceId) || data.DeviceId.Length < 8)
                {
                    string? guid = null;
                    try { guid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string; } catch { }
                    data.DeviceId = string.IsNullOrWhiteSpace(guid)
                        ? Guid.NewGuid().ToString("N")
                        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("screenX|" + guid))).ToLowerInvariant()[..32];
                    SaveFile();
                }
                return data.DeviceId;
            }
        }
    }

    public static string DeviceName => Environment.MachineName;

    // ---------------------------------------------------------------- HTTP

    internal static async Task<ApiResult> PostAsync(string path, object payload)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(BaseUrl + path, content).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonElement body = default;
            string? code = null, err = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                body = doc.RootElement.Clone();
                if (body.ValueKind == JsonValueKind.Object)
                {
                    if (body.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                    if (body.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) err = e.GetString();
                }
            }
            catch (JsonException) { } // HTML error page from a proxy etc.
            int status = (int)resp.StatusCode;
            // 5xx / non-JSON gateway errors are treated like being offline: keep whatever state we had.
            bool offline = status >= 500 || (body.ValueKind != JsonValueKind.Object && status >= 400);
            return new ApiResult(status, code, err ?? (offline ? "The licence server is not reachable right now." : null), body, offline);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new ApiResult(0, "offline", "Could not reach the licence server. Check your internet connection.", default, true);
        }
    }

    /// <summary>Update the stored state from a licence-related server answer. Returns true if it was a state-changing answer.</summary>
    internal static void ApplyServerVerdict(ApiResult r)
    {
        if (r.Offline) return;
        if (r.Status == 403 && r.Code == "revoked") SetState(LicenceState.Revoked);
        else if (r.Status == 401) SetState(LicenceState.Invalid);
    }

    // ---------------------------------------------------------------- activate / deactivate

    /// <summary>Activate this PC. On success the licence is stored and Pro unlocked.
    /// 409 device_limit: <see cref="ApiResult.Devices"/> lists the registered devices to pick one to deactivate.</summary>
    public static async Task<ApiResult> ActivateAsync(string licence)
    {
        licence = new string((licence ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        if (licence.Length == 0) return new ApiResult(0, "empty", "Paste your licence first.", default, false);

        var r = await PostAsync("/api/activate", new
        {
            licence,
            deviceId = DeviceId,
            name = DeviceName,
            os = OsName,
            version = Installer.Version,
        }).ConfigureAwait(false);

        if (r.Ok)
        {
            lock (Gate)
            {
                StoreKey(licence);
                data.State = nameof(LicenceState.Active);
                data.LastCheck = DateTime.UtcNow.ToString("O");
                SaveFile();
            }
            Changed?.Invoke();
        }
        else if (r.Status == 403 && r.Code == "revoked")
        {
            lock (Gate) { StoreKey(licence); SaveFile(); }
            SetState(LicenceState.Revoked);
        }
        return r;
    }

    /// <summary>Free a slot for <paramref name="deviceId"/> using the given licence (which may not be stored yet,
    /// e.g. while activation is blocked by the device limit).</summary>
    public static async Task<ApiResult> DeactivateAsync(string licence, string deviceId)
    {
        licence = new string((licence ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        var r = await PostAsync("/api/deactivate", new { licence, deviceId }).ConfigureAwait(false);
        if (r.Ok && string.Equals(deviceId, DeviceId, StringComparison.Ordinal) && string.Equals(licence, Key, StringComparison.Ordinal))
            ClearLocal();
        return r;
    }

    /// <summary>"Sign out": tell the server to free this device's slot (best effort) and forget the licence here.</summary>
    public static async Task SignOutAsync()
    {
        var key = Key;
        if (key != null) await DeactivateAsync(key, DeviceId).ConfigureAwait(false);
        ClearLocal();
    }

    static void ClearLocal()
    {
        lock (Gate) { StoreKey(null); data.State = nameof(LicenceState.None); SaveFile(); }
        Changed?.Invoke();
    }
}
