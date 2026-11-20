using System.Management;
using Optimizer.Core.Changes;

namespace Optimizer.Platform.Windows.Changes;

/// <summary>
/// Defender folder exclusions through the same WMI class the Add-MpPreference cmdlet uses.
/// Reading and writing exclusions requires administrator rights.
/// </summary>
internal static class DefenderExecutor
{
    private const string Namespace = @"root\Microsoft\Windows\Defender";

    public static DefenderExclusionChange Capture(DefenderExclusionChange desired) =>
        desired with { ExistedBefore = Contains(desired.Path), Captured = true };

    public static void Apply(DefenderExclusionChange change) => Invoke("Add", change.Path);

    public static void Revert(DefenderExclusionChange change)
    {
        // Never remove an exclusion the user had made themselves.
        if (change.ExistedBefore == false)
            Invoke("Remove", change.Path);
    }

    public static ChangeState Inspect(DefenderExclusionChange change)
    {
        var present = Contains(change.Path);
        if (change.ExistedBefore == true)
            return present ? ChangeState.Original : ChangeState.Diverged;
        return present ? ChangeState.Applied : ChangeState.Original;
    }

    private static bool Contains(string path)
    {
        using var searcher = new ManagementObjectSearcher(Namespace, "SELECT ExclusionPath FROM MSFT_MpPreference");
        using var results = searcher.Get();
        foreach (var mo in results.Cast<ManagementBaseObject>())
        {
            using (mo)
            {
                var paths = mo["ExclusionPath"] as string[] ?? [];
                // Without admin rights Defender returns a placeholder instead of the list.
                if (paths.Any(p => p.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)))
                    throw new UnauthorizedAccessException("Defender istisnaları yönetici izni olmadan okunamaz.");
                if (paths.Any(p => string.Equals(Normalize(p), Normalize(path), StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        return false;
    }

    private static void Invoke(string method, string path)
    {
        using var cls = new ManagementClass(Namespace, "MSFT_MpPreference", null);
        using var input = cls.GetMethodParameters(method);
        input["ExclusionPath"] = new[] { path };
        using var output = cls.InvokeMethod(method, input, null);
        var code = Convert.ToUInt32(output?["ReturnValue"] ?? 0u);
        if (code != 0)
            throw new InvalidOperationException($"Defender '{method}' işlemi başarısız (kod {code}). Tamper Protection ya da başka bir antivirüs engelliyor olabilir.");
    }

    private static string Normalize(string p) => p.Trim().TrimEnd('\\');
}

/// <summary>System Restore checkpoint, created before administrator-level changes.</summary>
public static class RestorePoint
{
    /// <returns>Null on success, otherwise a reason the point could not be created.</returns>
    public static string? TryCreate(string description)
    {
        try
        {
            using var cls = new ManagementClass(@"root\default", "SystemRestore", null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = 12u; // MODIFY_SETTINGS
            input["EventType"] = 100u;       // BEGIN_SYSTEM_CHANGE
            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            var code = Convert.ToUInt32(output?["ReturnValue"] ?? 0u);
            return code switch
            {
                0 => null,
                1058 or 0x80070422 => "Sistem Geri Yükleme bu bilgisayarda kapalı.",
                _ => $"Sistem Geri Yükleme noktası oluşturulamadı (kod {code}).",
            };
        }
        catch (ManagementException ex)
        {
            return $"Sistem Geri Yükleme noktası oluşturulamadı: {ex.Message}";
        }
    }
}
