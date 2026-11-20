using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using Optimizer.Core.Changes;

namespace Optimizer.Platform.Windows.Changes;

internal static class RegistryExecutor
{
    public static RegistryValueChange Capture(RegistryValueChange desired)
    {
        using var key = Root(desired.Hive).OpenSubKey(desired.Key);
        return desired with
        {
            Before = key is null ? null : Read(key, desired.Name),
            KeyExisted = key is not null,
            Captured = true,
        };
    }

    public static void Apply(RegistryValueChange change) => Write(change, change.After);

    public static void Revert(RegistryValueChange change)
    {
        Write(change, change.Before);

        if (change.KeyExisted)
            return;

        // We created the key; remove it again if we left it empty.
        using var key = Root(change.Hive).OpenSubKey(change.Key);
        if (key is { ValueCount: 0, SubKeyCount: 0 })
        {
            key.Dispose();
            Root(change.Hive).DeleteSubKey(change.Key, throwOnMissingSubKey: false);
        }
    }

    public static ChangeState Inspect(RegistryValueChange change)
    {
        using var key = Root(change.Hive).OpenSubKey(change.Key);
        var current = key is null ? null : Read(key, change.Name);

        if (current == change.After)
            return ChangeState.Applied;
        return current == change.Before ? ChangeState.Original : ChangeState.Diverged;
    }

    /// <summary>Exports the whole key with reg.exe so it can be restored by double-click even without this app.</summary>
    public static void Backup(RegistryValueChange change, string directory)
    {
        using (var key = Root(change.Hive).OpenSubKey(change.Key))
        {
            if (key is null)
                return;
        }

        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, string.Join("_", $"{change.Hive}_{change.Key}".Split(Path.GetInvalidFileNameChars().Append('\\').ToArray())) + ".reg");
        if (File.Exists(file))
            return;

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "reg.exe"),
            ArgumentList = { "export", $@"{change.Hive}\{change.Key}", file, "/y" },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("reg.exe başlatılamadı.");

        if (!process.WaitForExit(10_000) || process.ExitCode != 0)
            throw new InvalidOperationException($"reg export başarısız ({process.StandardError.ReadToEnd().Trim()}).");
    }

    private static void Write(RegistryValueChange change, RegistryData? data)
    {
        if (data is null)
        {
            using var existing = Root(change.Hive).OpenSubKey(change.Key, writable: true);
            existing?.DeleteValue(change.Name, throwOnMissingValue: false);
            return;
        }

        using var key = Root(change.Hive).CreateSubKey(change.Key, writable: true)
                        ?? throw new InvalidOperationException($"Anahtar açılamadı: {change.Hive}\\{change.Key}");

        switch (data.Kind)
        {
            case RegistryDataKind.Dword:
                key.SetValue(change.Name, int.Parse(data.Value, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                break;
            case RegistryDataKind.Qword:
                key.SetValue(change.Name, long.Parse(data.Value, CultureInfo.InvariantCulture), RegistryValueKind.QWord);
                break;
            case RegistryDataKind.String:
                key.SetValue(change.Name, data.Value, RegistryValueKind.String);
                break;
            case RegistryDataKind.ExpandString:
                key.SetValue(change.Name, data.Value, RegistryValueKind.ExpandString);
                break;
            case RegistryDataKind.MultiString:
                key.SetValue(change.Name, data.Value.Split('\n'), RegistryValueKind.MultiString);
                break;
            case RegistryDataKind.Binary:
                key.SetValue(change.Name, Convert.FromHexString(data.Value), RegistryValueKind.Binary);
                break;
            default:
                throw new NotSupportedException(data.Kind.ToString());
        }
    }

    internal static RegistryData? Read(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
            return null;

        return key.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => new RegistryData(RegistryDataKind.Dword, ((int)value).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.QWord => new RegistryData(RegistryDataKind.Qword, ((long)value).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.String => new RegistryData(RegistryDataKind.String, (string)value),
            RegistryValueKind.ExpandString => new RegistryData(RegistryDataKind.ExpandString, (string)value),
            RegistryValueKind.MultiString => new RegistryData(RegistryDataKind.MultiString, string.Join('\n', (string[])value)),
            RegistryValueKind.Binary => new RegistryData(RegistryDataKind.Binary, Convert.ToHexString((byte[])value)),
            var other => throw new NotSupportedException($"Desteklenmeyen kayıt türü: {other}"),
        };
    }

    internal static RegistryKey Root(string hive) => hive switch
    {
        "HKCU" => Registry.CurrentUser,
        "HKLM" => Registry.LocalMachine,
        _ => throw new NotSupportedException($"Desteklenmeyen kök: {hive}"),
    };
}
