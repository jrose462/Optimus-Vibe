using System.CommandLine;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Driver;
using OptimusVibe.Engine;
using OptimusVibe.Logging;
using OptimusVibe.Updates;

namespace OptimusVibe;

/// <summary>
/// `driver status` and `driver test-amd`: read-only diagnostics for the GPU-driver settings backends (NVIDIA DRS, AMD ADLX).
/// Registered in Program.Main.
/// </summary>
internal static class DriverCommands
{
    public static Command DriverCommand()
    {
        var cmd = new Command("driver", "Diagnose GPU driver settings support (NVIDIA DRS / AMD ADLX). Nothing is changed unless --apply-and-restore is passed.");
        var vendor = new Option<string?>("--vendor") { Description = "nvidia | amd | none. Defaults to this machine's device profile, else the detected GPU." };

        var status = new Command("status", "Show the driver backend, its availability, versions and every readable setting. Read-only.") { vendor };
        status.SetAction(pr =>
        {
            try
            {
                var v = vendor is null ? null : pr.GetValue(vendor);
                var resolved = string.IsNullOrWhiteSpace(v) ? DetectVendor(pr) : v!;
                Console.WriteLine($"optimus-vibe {AppVersion.Current} on {Environment.OSVersion.VersionString}, {(Environment.Is64BitProcess ? "x64" : "32-bit")}");
                foreach (var line in DriverDiagnostics.Status(resolved)) Console.WriteLine(line);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("driver status failed", ex);
                return 1;
            }
        });

        var dry = new Option<bool>("--dry-run") { Description = "Read only: report which harmless setting would be used (the default)." };
        var apply = new Option<bool>("--apply-and-restore")
        {
            Description = "Write one harmless value (image sharpness +/- 1), read it back, restore the original, read back again.",
        };
        var test = new Command("test-amd", "Self-test the AMD ADLX read / apply / restore path. Run it on the ROG Xbox Ally X.") { dry, apply };
        test.SetAction(pr =>
        {
            try
            {
                var doApply = pr.GetValue(apply);
                if (doApply && pr.GetValue(dry))
                {
                    Console.Error.WriteLine("Use either --dry-run or --apply-and-restore, not both.");
                    return 2;
                }
                var lines = DriverDiagnostics.TestAmd(doApply, out var passed);
                foreach (var line in lines) Console.WriteLine(line);
                return passed ? 0 : 1;
            }
            catch (Exception ex)
            {
                Log.Error("driver test-amd failed", ex);
                return 1;
            }
        });

        cmd.Subcommands.Add(status);
        cmd.Subcommands.Add(test);
        return cmd;
    }

    private static string DetectVendor(ParseResult pr)
    {
        try
        {
            var store = new ConfigStore(AppPaths.Resolve(pr.GetValue<string?>("--config")));
            return GameContext.SelectDevice(store, pr.GetValue<string?>("--profile")).Driver;
        }
        catch (Exception)
        {
            return DeviceDetector.DriverFor(null, DeviceDetector.Detect());
        }
    }
}
