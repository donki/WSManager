using System.Globalization;
using System.Text;

namespace SocWsManager.Output;

/// <summary>
/// Lleva la salida de la aplicación (una tubería) a su <see cref="LogFile"/>, línea a línea: con
/// <c>AppTimestampLog</c> pone la hora delante de cada línea. Un trozo sin salto de línea se escribe
/// igualmente si no llega nada más en <see cref="FlushDelay"/> (un «Pulse una tecla…» no se queda
/// retenido); entonces la línea sigue sin hora repetida.
/// </summary>
public sealed class OutputPump
{
    public static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(250);

    private readonly Stream _source;
    private readonly LogFile _target;
    private readonly bool _timestamps;
    private readonly Func<DateTime> _now;
    private readonly List<byte> _pending = [];
    private bool _atLineStart = true;

    public OutputPump(Stream source, LogFile target, bool timestamps, Func<DateTime>? now = null)
    {
        _source = source;
        _target = target;
        _timestamps = timestamps;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Bombea hasta que la tubería se cierra (la aplicación sale).</summary>
    public async Task RunAsync()
    {
        var buffer = new byte[8192];
        Task<int>? read = null;
        while (true)
        {
            read ??= _source.ReadAsync(buffer, 0, buffer.Length);
            if (_pending.Count > 0 && await Task.WhenAny(read, Task.Delay(FlushDelay)) != read)
            {
                FlushPartial();
                continue;
            }
            int n;
            try
            {
                n = await read;
            }
            catch (IOException)
            {
                n = 0;   // tubería rota: la aplicación ha salido
            }
            catch (ObjectDisposedException)
            {
                n = 0;
            }
            read = null;
            if (n == 0)
                break;
            Feed(buffer.AsSpan(0, n));
        }
        FlushPartial();
    }

    /// <summary>Procesa un trozo: las líneas completas se escriben; el resto espera.</summary>
    internal void Feed(ReadOnlySpan<byte> data)
    {
        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n')
                continue;
            _pending.AddRange(data[start..(i + 1)]);
            WriteChunk(complete: true);
            start = i + 1;
        }
        if (start < data.Length)
            _pending.AddRange(data[start..]);
    }

    internal void FlushPartial()
    {
        if (_pending.Count > 0)
            WriteChunk(complete: false);
    }

    private void WriteChunk(bool complete)
    {
        byte[] bytes;
        if (_timestamps && _atLineStart)
        {
            var stamp = Encoding.ASCII.GetBytes(_now().ToString("yyyy-MM-dd HH:mm:ss.fff ", CultureInfo.InvariantCulture));
            bytes = new byte[stamp.Length + _pending.Count];
            stamp.CopyTo(bytes, 0);
            _pending.CopyTo(bytes, stamp.Length);
        }
        else
        {
            bytes = [.. _pending];
        }
        _pending.Clear();
        _target.Write(bytes);
        _atLineStart = complete;
    }
}
