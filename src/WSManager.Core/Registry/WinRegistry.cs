using Microsoft.Win32;

namespace SocWsManager.Registry;

/// <summary>
/// El registro de Windows bajo una raíz: <c>HKLM\SYSTEM\CurrentControlSet\Services</c> en producción,
/// <c>HKCU\Software\sOCWSManagerTests\&lt;guid&gt;</c> en las pruebas. Las cadenas expandibles se leen
/// sin expandir (se expanden al usarlas, como el original).
/// </summary>
public sealed class WinRegistry : IRegistry
{
    public const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    private readonly RegistryKey _hive;
    private readonly string _base;

    public WinRegistry(RegistryKey hive, string basePath)
    {
        _hive = hive;
        _base = basePath.Trim('\\');
    }

    /// <summary>Los servicios de verdad.</summary>
    public static WinRegistry Services() => new(Microsoft.Win32.Registry.LocalMachine, ServicesPath);

    public string BasePath => _base;

    private string Full(string path) => RegistryExtensions.Combine(_base, path);

    private RegistryKey? Open(string path, bool write = false) => _hive.OpenSubKey(Full(path), write);

    public bool KeyExists(string path)
    {
        using var k = Open(path);
        return k is not null;
    }

    public void CreateKey(string path)
    {
        using var k = _hive.CreateSubKey(Full(path), true);
    }

    public void DeleteKeyTree(string path) => _hive.DeleteSubKeyTree(Full(path), throwOnMissingSubKey: false);

    public IReadOnlyList<string> SubKeys(string path)
    {
        using var k = Open(path);
        return k?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyList<string> ValueNames(string path)
    {
        using var k = Open(path);
        return k?.GetValueNames() ?? [];
    }

    public RegValue? Get(string path, string name)
    {
        using var k = Open(path);
        if (k is null)
            return null;
        var raw = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
            return null;
        return k.GetValueKind(name) switch
        {
            RegistryValueKind.String => new RegValue(RegKind.String, (string)raw),
            RegistryValueKind.ExpandString => new RegValue(RegKind.ExpandString, (string)raw),
            RegistryValueKind.MultiString => new RegValue(RegKind.MultiString, (string[])raw),
            RegistryValueKind.DWord => new RegValue(RegKind.DWord, (int)raw),
            RegistryValueKind.QWord => new RegValue(RegKind.QWord, (long)raw),
            _ => raw is byte[] b ? new RegValue(RegKind.Binary, b) : new RegValue(RegKind.String, raw.ToString() ?? string.Empty),
        };
    }

    public void Set(string path, string name, RegValue value)
    {
        using var k = _hive.CreateSubKey(Full(path), true);
        var kind = value.Kind switch
        {
            RegKind.String => RegistryValueKind.String,
            RegKind.ExpandString => RegistryValueKind.ExpandString,
            RegKind.MultiString => RegistryValueKind.MultiString,
            RegKind.DWord => RegistryValueKind.DWord,
            RegKind.QWord => RegistryValueKind.QWord,
            _ => RegistryValueKind.Binary,
        };
        k.SetValue(name, value.Data, kind);
    }

    public void Delete(string path, string name)
    {
        using var k = Open(path, write: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }
}
