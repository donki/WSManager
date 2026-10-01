namespace SocWsManager.Registry;

public enum RegKind
{
    String,
    ExpandString,
    MultiString,
    DWord,
    QWord,
    Binary,
}

/// <summary>Un valor del registro con su tipo. <see cref="Data"/> es string, string[], int, long o byte[].</summary>
public sealed record RegValue(RegKind Kind, object Data)
{
    public static RegValue Str(string s) => new(RegKind.String, s);
    public static RegValue Expand(string s) => new(RegKind.ExpandString, s);
    public static RegValue Multi(IEnumerable<string> lines) => new(RegKind.MultiString, lines.ToArray());
    public static RegValue DWord(int n) => new(RegKind.DWord, n);
    public static RegValue QWord(long n) => new(RegKind.QWord, n);

    /// <summary>Como texto (las listas, unidas por saltos de línea; los números, en decimal).</summary>
    public string AsString() => Data switch
    {
        string s => s,
        string[] a => string.Join(Environment.NewLine, a),
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        byte[] b => Convert.ToHexString(b),
        _ => string.Empty,
    };

    /// <summary>Como número; los textos numéricos también valen (el original a veces los escribe así).</summary>
    public long? AsNumber() => Data switch
    {
        int i => (uint)i,
        long l => l,
        string s when long.TryParse(s.Trim(), out var n) => n,
        _ => null,
    };

    public string[] AsLines() => Data switch
    {
        string[] a => a,
        string s when s.Length > 0 => s.Split(["\r\n", "\n"], StringSplitOptions.None),
        _ => [],
    };

    public bool Equals(RegValue? other) =>
        other is not null && Kind == other.Kind && AsString() == other.AsString();

    public override int GetHashCode() => HashCode.Combine(Kind, AsString());
}

/// <summary>
/// El registro visto desde una raíz (rutas relativas con «\»). Hay una implementación real
/// (<see cref="WinRegistry"/>) y otra en memoria (<see cref="MemoryRegistry"/>) para las pruebas y el
/// modo aislado. El nombre de valor vacío es el valor por defecto de la clave.
/// </summary>
public interface IRegistry
{
    bool KeyExists(string path);
    void CreateKey(string path);
    void DeleteKeyTree(string path);
    IReadOnlyList<string> SubKeys(string path);
    IReadOnlyList<string> ValueNames(string path);
    RegValue? Get(string path, string name);
    void Set(string path, string name, RegValue value);
    void Delete(string path, string name);
}

/// <summary>Ayudas sobre <see cref="IRegistry"/>.</summary>
public static class RegistryExtensions
{
    public static string? GetString(this IRegistry r, string path, string name) => r.Get(path, name)?.AsString();

    public static long? GetNumber(this IRegistry r, string path, string name) => r.Get(path, name)?.AsNumber();

    public static string[] GetLines(this IRegistry r, string path, string name) => r.Get(path, name)?.AsLines() ?? [];

    public static string Combine(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a.TrimEnd('\\') + "\\" + b.TrimStart('\\');
}
