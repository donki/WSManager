using System.Globalization;

namespace SocWsManager.Output;

/// <summary>
/// Un fichero de salida (stdout o stderr) con la disposición de creación del original (RF-05) y la
/// rotación (RF-06). Si stdout y stderr van al mismo fichero comparten instancia: cada escritura es
/// atómica bajo un cerrojo, así que las líneas no se mezclan a medias (CL-07).
/// </summary>
public sealed class LogFile : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private FileStream? _stream;
    private DateTime _openedUtc;

    public string Path { get; }
    public int Disposition { get; }
    public long RotateBytes { get; set; }
    public int RotateSeconds { get; set; }

    /// <summary>Rotar en marcha al pasar los límites (AppRotateOnline != 0).</summary>
    public bool RotateOnline { get; set; }

    /// <summary>Antes y después de rotar (para los ganchos Rotate/Pre y Rotate/Post); recibe la ruta nueva del viejo.</summary>
    public Action<string>? Rotating { get; set; }
    public Action<string>? Rotated { get; set; }

    public long Length { get; private set; }

    private LogFile(string path, int disposition, Func<DateTime> now)
    {
        Path = path;
        Disposition = disposition;
        _now = now;
    }

    /// <summary>
    /// Abre (o crea) el fichero con la disposición: 1 CREATE_NEW (falla si existe), 2 CREATE_ALWAYS
    /// (vacía), 3 OPEN_EXISTING (falla si no existe), 4 OPEN_ALWAYS (añade, por defecto), 5
    /// TRUNCATE_EXISTING (vacía; falla si no existe). Crea la carpeta si falta (CL-08).
    /// </summary>
    public static LogFile Open(string path, int disposition, Func<DateTime>? now = null)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (System.IO.Path.GetDirectoryName(full) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        var log = new LogFile(full, disposition, now ?? (() => DateTime.UtcNow));
        log.OpenStream(disposition);
        return log;
    }

    private void OpenStream(int disposition)
    {
        var mode = disposition switch
        {
            1 => FileMode.CreateNew,
            2 => FileMode.Create,
            3 => FileMode.Open,
            5 => FileMode.Truncate,
            _ => FileMode.OpenOrCreate,
        };
        _stream = new FileStream(Path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
        _stream.Seek(0, SeekOrigin.End);
        Length = _stream.Length;
        _openedUtc = _now();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_stream is null)
                return;
            if (RotateOnline && ShouldRotate())
                RotateLocked();
            _stream!.Write(data);
            _stream.Flush();
            Length += data.Length;
        }
    }

    private bool ShouldRotate() =>
        (RotateBytes > 0 && Length >= RotateBytes)
        || (RotateSeconds > 0 && Length > 0 && _now() - _openedUtc >= TimeSpan.FromSeconds(RotateSeconds));

    /// <summary>Rota ya (bajo demanda). Devuelve la ruta a la que se movió lo viejo, o null si estaba vacío.</summary>
    public string? RotateNow()
    {
        lock (_gate)
            return Length == 0 ? null : RotateLocked();
    }

    private string RotateLocked()
    {
        var target = Rotation.RotatedName(Path, _now().ToLocalTime());
        Rotating?.Invoke(target);
        _stream?.Dispose();
        _stream = null;
        File.Move(Path, target);
        OpenStream(2);
        Rotated?.Invoke(target);
        return target;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}

/// <summary>Rotación al arrancar y nombres de los ficheros rotados.</summary>
public static class Rotation
{
    /// <summary>
    /// <c>C:\logs\app.log</c> → <c>C:\logs\app-20261001T134501.123.log</c>. Si ya existe (dos en el mismo
    /// milisegundo), se añade <c>-1</c>, <c>-2</c>… (CL-10).
    /// </summary>
    public static string RotatedName(string path, DateTime localTime)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var stamp = localTime.ToString("yyyyMMdd'T'HHmmss.fff", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(dir, $"{name}-{stamp}{ext}");
        for (var i = 1; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name}-{stamp}-{i}{ext}");
        return candidate;
    }

    /// <summary>
    /// Al arrancar el servicio con <c>AppRotateFiles</c>: rota el fichero si existe, no está vacío y
    /// pasa de los límites; con los dos límites a 0, siempre (CL-09). Devuelve la ruta nueva o null.
    /// </summary>
    public static string? RotateAtStart(string path, int rotateSeconds, long rotateBytes, DateTime nowUtc)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            return null;
        var tooOld = rotateSeconds > 0 && nowUtc - info.CreationTimeUtc >= TimeSpan.FromSeconds(rotateSeconds);
        var tooBig = rotateBytes > 0 && info.Length >= rotateBytes;
        var always = rotateSeconds == 0 && rotateBytes == 0;
        if (!(always || tooOld || tooBig))
            return null;
        var target = RotatedName(info.FullName, nowUtc.ToLocalTime());
        File.Move(info.FullName, target);
        return target;
    }
}
