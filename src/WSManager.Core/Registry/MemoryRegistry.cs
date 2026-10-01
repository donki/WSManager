using System.Text;

namespace SocWsManager.Registry;

/// <summary>
/// Un registro en memoria (sin distinguir mayúsculas, como el de Windows). Lo usan las pruebas y el
/// modo aislado, que lo guarda en un fichero de texto en su carpeta.
/// </summary>
public sealed class MemoryRegistry : IRegistry
{
    private sealed class Node
    {
        public Dictionary<string, Node> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RegValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> KeyOrder { get; } = [];
    }

    private readonly Node _root = new();
    private readonly object _gate = new();

    /// <summary>Se llama tras cada cambio (el modo aislado guarda aquí).</summary>
    public Action? Changed { get; set; }

    private static string[] Split(string path) => path.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    private Node? Find(string path)
    {
        var n = _root;
        foreach (var part in Split(path))
            if (!n.Keys.TryGetValue(part, out n!))
                return null;
        return n;
    }

    private Node Ensure(string path)
    {
        var n = _root;
        foreach (var part in Split(path))
        {
            if (!n.Keys.TryGetValue(part, out var child))
            {
                child = new Node();
                n.Keys[part] = child;
                n.KeyOrder.Add(part);
            }
            n = child;
        }
        return n;
    }

    public bool KeyExists(string path)
    {
        lock (_gate)
            return Find(path) is not null;
    }

    public void CreateKey(string path)
    {
        lock (_gate)
            Ensure(path);
        Changed?.Invoke();
    }

    public void DeleteKeyTree(string path)
    {
        lock (_gate)
        {
            var parts = Split(path);
            if (parts.Length == 0)
                return;
            var parent = Find(string.Join('\\', parts[..^1]));
            if (parent is null)
                return;
            var name = parent.KeyOrder.FirstOrDefault(k => k.Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
            if (name is null)
                return;
            parent.Keys.Remove(name);
            parent.KeyOrder.Remove(name);
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<string> SubKeys(string path)
    {
        lock (_gate)
            return Find(path)?.KeyOrder.ToArray() ?? [];
    }

    public IReadOnlyList<string> ValueNames(string path)
    {
        lock (_gate)
            return Find(path)?.Values.Keys.ToArray() ?? [];
    }

    public RegValue? Get(string path, string name)
    {
        lock (_gate)
            return Find(path) is { } n && n.Values.TryGetValue(name, out var v) ? v : null;
    }

    public void Set(string path, string name, RegValue value)
    {
        lock (_gate)
            Ensure(path).Values[name] = value;
        Changed?.Invoke();
    }

    public void Delete(string path, string name)
    {
        lock (_gate)
            Find(path)?.Values.Remove(name);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ texto
    //
    // Formato propio, de una línea por valor, para no depender de reflexión (NativeAOT):
    //   [ruta\de\la\clave]
    //   nombre<TAB>tipo<TAB>dato        (listas con \u001F entre líneas; el nombre vacío es «@»)

    public string Serialize()
    {
        var sb = new StringBuilder();
        lock (_gate)
            Write(_root, string.Empty, sb);
        return sb.ToString();
    }

    private static void Write(Node node, string path, StringBuilder sb)
    {
        if (path.Length > 0)
        {
            sb.Append('[').Append(path).Append(']').Append('\n');
            foreach (var (name, value) in node.Values)
            {
                var data = value.Data is string[] lines ? string.Join('\u001F', lines) : value.AsString();
                sb.Append(name.Length == 0 ? "@" : Escape(name)).Append('\t').Append(value.Kind).Append('\t').Append(Escape(data)).Append('\n');
            }
        }
        foreach (var key in node.KeyOrder)
            Write(node.Keys[key], path.Length == 0 ? key : path + "\\" + key, sb);
    }

    public static MemoryRegistry Parse(string text)
    {
        var reg = new MemoryRegistry();
        string? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = line[1..^1];
                reg.Ensure(current);
                continue;
            }
            if (current is null)
                continue;
            var parts = line.Split('\t');
            if (parts.Length != 3 || !Enum.TryParse<RegKind>(parts[1], out var kind))
                continue;
            var name = parts[0] == "@" ? string.Empty : Unescape(parts[0]);
            var data = Unescape(parts[2]);
            RegValue value = kind switch
            {
                RegKind.MultiString => new RegValue(kind, data.Length == 0 ? Array.Empty<string>() : data.Split('\u001F')),
                RegKind.DWord => new RegValue(kind, int.TryParse(data, out var i) ? i : (int)(uint.TryParse(data, out var u) ? u : 0)),
                RegKind.QWord => new RegValue(kind, long.TryParse(data, out var l) ? l : 0L),
                RegKind.Binary => new RegValue(kind, Convert.FromHexString(data)),
                _ => new RegValue(kind, data),
            };
            reg.Ensure(current).Values[name] = value;
        }
        return reg;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => s[i] });
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }
}
