using System;
using System.IO;
using System.Threading.Tasks;

namespace ScreenX;

/// <summary>
/// Headless check of the licence client against a running server: <c>screenX.exe --selftest-licence &lt;licence&gt;</c>
/// with SCREENX_API_URL and SCREENX_DATA_DIR set. Exit code 0 = all good. Never launches an installer.
/// </summary>
public static class LicenceSelfTest
{
    public static async Task<int> RunAsync(string licence)
    {
        int fails = 0;
        var log = new System.Text.StringBuilder();
        void Check(string what, bool ok, string extra = "") { if (!ok) fails++; log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what} {extra}"); }
        try
        {
            Check("device id is 32+ hex chars and stable", Licence.DeviceId.Length >= 32 && Licence.DeviceId == Licence.DeviceId);
            Check("device id is not the raw MachineGuid", !Licence.DeviceId.Contains('-'));
            Check("starts locked", !Licence.IsPro);

            var bad = await Licence.ActivateAsync("not-a-licence");
            Check("bad licence rejected", !bad.Ok && !Licence.IsPro, $"({bad.Status} {bad.Code})");

            var a = await Licence.ActivateAsync(licence);
            if (a.Status == 409)
            {
                var devs = a.Devices();
                Check("409 device_limit lists devices", a.Code == "device_limit" && devs.Count > 0, $"({devs.Count} devices)");
                var d = await Licence.DeactivateAsync(licence, devs[0].Id);
                Check("deactivate another device", d.Ok);
                a = await Licence.ActivateAsync(licence);
            }
            Check("activate", a.Ok && Licence.IsPro, $"({a.Status} {a.Code})");
            Check("licence stored encrypted (not plain in file)",
                !File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("SCREENX_DATA_DIR")!, "licence.json")).Contains(licence));
            Check("licence readable back", Licence.Key == licence);

            var u = await Updater.CheckAsync();
            log.AppendLine($"      update: {u.Message}");
            Check("update check answered", u.Checked);
            if (u.UpdateAvailable) Check("update installer downloaded and sha256 verified", u.InstallerPath != null && File.Exists(u.InstallerPath), "(" + u.Message + ")");

            await Licence.SignOutAsync();
            Check("sign out locks again", !Licence.IsPro && Licence.Key == null);
        }
        catch (Exception ex) { fails++; log.AppendLine("EXCEPTION " + ex); }
        string outFile = Path.Combine(Environment.GetEnvironmentVariable("SCREENX_DATA_DIR") ?? Path.GetTempPath(), "licence-selftest.log");
        File.WriteAllText(outFile, log.ToString());
        return fails;
    }
}
