using System.Text;
using SocWsManager.Output;

namespace SocWsManager.Tests;

public sealed class LogFileTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static void Write(LogFile log, string text) => log.Write(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void OPEN_ALWAYS_añade_al_final_y_crea_la_carpeta()
    {
        var path = _dir.File(@"sub\carpeta\out.log");
        File.WriteAllText(path, "viejo\n");
        using (var log = LogFile.Open(path, 4))
            Write(log, "nuevo\n");
        Assert.Equal("viejo\nnuevo\n", File.ReadAllText(path));

        var fresh = Path.Combine(_dir.Path, "no", "existe", "x.log");
        using (var log = LogFile.Open(fresh, 4))
            Write(log, "a");
        Assert.Equal("a", File.ReadAllText(fresh));
    }

    [Fact]
    public void CREATE_ALWAYS_y_TRUNCATE_EXISTING_vacian()
    {
        var path = _dir.File("out.log", "viejo");
        using (var log = LogFile.Open(path, 2)) Write(log, "1");
        Assert.Equal("1", File.ReadAllText(path));
        using (var log = LogFile.Open(path, 5)) Write(log, "2");
        Assert.Equal("2", File.ReadAllText(path));
    }

    [Fact]
    public void CREATE_NEW_falla_si_existe_y_OPEN_EXISTING_si_no()
    {
        var path = _dir.File("out.log", "x");
        Assert.Throws<IOException>(() => LogFile.Open(path, 1));
        Assert.Throws<FileNotFoundException>(() => LogFile.Open(_dir.File("nada.log"), 3));
        Assert.Throws<FileNotFoundException>(() => LogFile.Open(_dir.File("nada2.log"), 5));
        using (var log = LogFile.Open(path, 3)) Write(log, "y");
        Assert.Equal("xy", File.ReadAllText(path));
        using (var log = LogFile.Open(_dir.File("nuevo.log"), 1)) Write(log, "z");
    }

    [Fact]
    public void Se_puede_leer_mientras_se_escribe()
    {
        var path = _dir.File("out.log");
        using var log = LogFile.Open(path, 4);
        Write(log, "en marcha\n");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal("en marcha\n", new StreamReader(reader).ReadToEnd());
    }

    [Fact]
    public void Rota_en_marcha_al_pasar_de_tamaño()
    {
        var path = _dir.File("out.log");
        var rotated = new List<string>();
        var pre = 0;
        using (var log = LogFile.Open(path, 4))
        {
            log.RotateOnline = true;
            log.RotateBytes = 10;
            log.Rotating = _ => pre++;
            log.Rotated = rotated.Add;
            Write(log, "0123456789");   // 10: aún no
            Write(log, "abc");          // antes de escribir ya hay 10 → rota
            Assert.Equal(3, log.Length);
        }
        Assert.Single(rotated);
        Assert.Equal(1, pre);
        Assert.Equal("0123456789", File.ReadAllText(rotated[0]));
        Assert.Equal("abc", File.ReadAllText(path));
        Assert.Matches(@"out-\d{8}T\d{6}\.\d{3}\.log$", rotated[0]);
    }

    [Fact]
    public void Rota_en_marcha_al_pasar_de_tiempo()
    {
        var now = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var path = _dir.File("t.log");
        using var log = LogFile.Open(path, 4, () => now);
        log.RotateOnline = true;
        log.RotateSeconds = 60;
        Write(log, "uno\n");
        now = now.AddSeconds(30);
        Write(log, "dos\n");
        Assert.Single(Directory.GetFiles(_dir.Path));
        now = now.AddSeconds(31);
        Write(log, "tres\n");
        Assert.Equal(2, Directory.GetFiles(_dir.Path).Length);
        Assert.Equal("tres\n", ReadShared(path));
    }

    [Fact]
    public void Sin_rotacion_en_marcha_no_rota_aunque_pase()
    {
        var path = _dir.File("n.log");
        using var log = LogFile.Open(path, 4);
        log.RotateBytes = 1;
        Write(log, "aaaa");
        Write(log, "bbbb");
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public void Rotar_bajo_demanda()
    {
        var path = _dir.File("d.log");
        using var log = LogFile.Open(path, 4);
        Assert.Null(log.RotateNow());   // vacío: nada que rotar
        Write(log, "algo");
        var target = log.RotateNow();
        Assert.NotNull(target);
        Assert.Equal("algo", File.ReadAllText(target!));
        Assert.Equal(0, log.Length);
        log.Dispose();
        Write(log, "tras cerrar");   // no lanza
    }

    [Fact]
    public void Nombres_rotados_unicos_en_el_mismo_milisegundo()
    {
        var t = new DateTime(2026, 10, 1, 13, 45, 1, 123);
        var path = Path.Combine(_dir.Path, "app.log");
        var first = Rotation.RotatedName(path, t);
        Assert.EndsWith("app-20261001T134501.123.log", first);
        File.WriteAllText(first, "");
        var second = Rotation.RotatedName(path, t);
        Assert.EndsWith("app-20261001T134501.123-1.log", second);
        File.WriteAllText(second, "");
        Assert.EndsWith("-2.log", Rotation.RotatedName(path, t));
        Assert.EndsWith("sinext-20261001T134501.123", Rotation.RotatedName(Path.Combine(_dir.Path, "sinext"), t));
    }

    [Fact]
    public void Rotar_al_arrancar_segun_limites()
    {
        var now = DateTime.UtcNow;
        var path = _dir.File("s.log", new string('x', 100));
        Assert.Null(Rotation.RotateAtStart(path, 0, 1000, now));              // pequeño
        Assert.Null(Rotation.RotateAtStart(path, 3600, 0, now));              // joven
        Assert.NotNull(Rotation.RotateAtStart(path, 0, 50, now));             // grande
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "y");
        Assert.NotNull(Rotation.RotateAtStart(path, 0, 0, now));              // sin límites: siempre (CL-09)
        File.WriteAllText(path, "z");
        Assert.NotNull(Rotation.RotateAtStart(path, 10, 0, now.AddHours(1))); // viejo
        File.WriteAllText(path, "");
        Assert.Null(Rotation.RotateAtStart(path, 0, 0, now));                 // vacío
        Assert.Null(Rotation.RotateAtStart(Path.Combine(_dir.Path, "no.log"), 0, 0, now));
    }

    private static string ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }
}

public sealed class OutputPumpTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly DateTime Clock = new(2026, 10, 1, 9, 5, 7, 42);

    private string Read(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public async Task Copia_todo_y_pone_hora_en_cada_linea()
    {
        var path = _dir.File("o.log");
        using var log = LogFile.Open(path, 4);
        var source = new MemoryStream(Encoding.UTF8.GetBytes("uno\r\ndos\ntres"));
        await new OutputPump(source, log, timestamps: true, () => Clock).RunAsync();
        Assert.Equal("2026-10-01 09:05:07.042 uno\r\n2026-10-01 09:05:07.042 dos\n2026-10-01 09:05:07.042 tres", Read(path));
    }

    [Fact]
    public async Task Sin_hora_es_copia_exacta()
    {
        var path = _dir.File("o.log");
        using var log = LogFile.Open(path, 4);
        var bytes = new byte[20000];
        new Random(1).NextBytes(bytes);
        await new OutputPump(new MemoryStream(bytes), log, timestamps: false).RunAsync();
        log.Dispose();
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Un_trozo_sin_salto_sale_y_la_linea_sigue_sin_repetir_hora()
    {
        var path = _dir.File("p.log");
        using var log = LogFile.Open(path, 4);
        var pump = new OutputPump(Stream.Null, log, timestamps: true, () => Clock);
        pump.Feed("Pulse una tecla"u8);
        Assert.Equal("", Read(path));      // aún retenido
        pump.FlushPartial();               // pasa el tiempo de espera
        pump.Feed("... ok\nsiguiente\n"u8);
        Assert.Equal("2026-10-01 09:05:07.042 Pulse una tecla... ok\n2026-10-01 09:05:07.042 siguiente\n", Read(path));
    }

    [Fact]
    public async Task Lo_parcial_se_escribe_si_no_llega_mas()
    {
        var path = _dir.File("q.log");
        using var log = LogFile.Open(path, 4);
        var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In);
        var writer = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.Out, pipe.ClientSafePipeHandle);
        var run = new OutputPump(pipe, log, timestamps: false).RunAsync();
        writer.Write("> "u8);
        writer.Flush();
        Assert.True(Wait.Until(() => Read(path) == "> ", 3000));
        writer.Dispose();
        await run;
    }

    [Fact]
    public async Task Dos_salidas_al_mismo_fichero_no_mezclan_lineas()
    {
        var path = _dir.File("m.log");
        using var log = LogFile.Open(path, 4);
        string Lines(string tag) => string.Concat(Enumerable.Range(0, 500).Select(i => $"{tag}{i:000}{new string('.', 40)}\n"));
        await Task.WhenAll(
            Task.Run(() => new OutputPump(new MemoryStream(Encoding.UTF8.GetBytes(Lines("A"))), log, false).RunAsync()),
            Task.Run(() => new OutputPump(new MemoryStream(Encoding.UTF8.GetBytes(Lines("B"))), log, false).RunAsync()));
        var lines = Read(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1000, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^[AB]\d{3}\.{40}$", l));
    }
}
