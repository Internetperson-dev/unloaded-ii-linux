using System.Diagnostics;
using Reloaded.Memory.Sources;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;

namespace ReloadedDropIn.Adapter.P5R;

/// Skips the Persona 5 Royal startup logos and opening cinematic.

internal static class SkipIntroPatch
{
    private const string Signature =
        "74 10 C7 ?? 0C 00 00 00";

    private static readonly byte[] PatchBytes =
    [
        0x90,
        0x90
    ];

    public static bool Apply(out string message)
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var module = process.MainModule;

            if (module is null)
            {
                message = "Skip Intro: could not access the game module.";
                return false;
            }

            using var scanner = new Scanner(
                process,
                module);

            var offset = scanner.FindPattern(Signature);

            if (offset < 0)
            {
                message =
                    $"Skip Intro: signature not found: {Signature}";
                return false;
            }

            var address =
                (nuint)module.BaseAddress + (nuint)offset;

            Memory.Instance.SafeWriteRaw(
                address,
                PatchBytes);

            message =
                $"Skip Intro: patch applied at 0x{address:X}.";

            return true;
        }
        catch (Exception ex)
        {
            message =
                $"Skip Intro: patch failed: {ex.Message}";
            return false;
        }
    }
}

