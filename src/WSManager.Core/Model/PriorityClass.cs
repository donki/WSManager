namespace SocWsManager.Model;

/// <summary>Las seis clases de prioridad de Windows, con su valor numérico (el que va en el registro).</summary>
public enum PriorityClass : uint
{
    Idle = 0x40,
    BelowNormal = 0x4000,
    Normal = 0x20,
    AboveNormal = 0x8000,
    High = 0x80,
    Realtime = 0x100,
}

public static class Priorities
{
    /// <summary>De más alta a más baja, para listas.</summary>
    public static readonly IReadOnlyList<PriorityClass> All =
    [
        PriorityClass.Realtime, PriorityClass.High, PriorityClass.AboveNormal,
        PriorityClass.Normal, PriorityClass.BelowNormal, PriorityClass.Idle,
    ];

    /// <summary>El nombre de la constante de Windows (<c>HIGH_PRIORITY_CLASS</c>), como en la línea de órdenes del original.</summary>
    public static string ToConstant(PriorityClass p) => p switch
    {
        PriorityClass.Realtime => "REALTIME_PRIORITY_CLASS",
        PriorityClass.High => "HIGH_PRIORITY_CLASS",
        PriorityClass.AboveNormal => "ABOVE_NORMAL_PRIORITY_CLASS",
        PriorityClass.BelowNormal => "BELOW_NORMAL_PRIORITY_CLASS",
        PriorityClass.Idle => "IDLE_PRIORITY_CLASS",
        _ => "NORMAL_PRIORITY_CLASS",
    };

    /// <summary>Del nombre (con o sin <c>_PRIORITY_CLASS</c>) o del número; null si no es ninguno.</summary>
    public static PriorityClass? Parse(string text)
    {
        var t = text.Trim().ToUpperInvariant().Replace(' ', '_');
        if (t.EndsWith("_PRIORITY_CLASS", StringComparison.Ordinal))
            t = t[..^"_PRIORITY_CLASS".Length];
        switch (t)
        {
            case "REALTIME": return PriorityClass.Realtime;
            case "HIGH": return PriorityClass.High;
            case "ABOVE_NORMAL" or "ABOVENORMAL": return PriorityClass.AboveNormal;
            case "NORMAL": return PriorityClass.Normal;
            case "BELOW_NORMAL" or "BELOWNORMAL": return PriorityClass.BelowNormal;
            case "IDLE": return PriorityClass.Idle;
        }
        return FromNumber(t);
    }

    private static PriorityClass? FromNumber(string t)
    {
        uint value;
        if (t.StartsWith("0X", StringComparison.Ordinal))
        {
            if (!uint.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out value))
                return null;
        }
        else if (!uint.TryParse(t, out value))
        {
            return null;
        }
        return Enum.IsDefined(typeof(PriorityClass), value) ? (PriorityClass)value : null;
    }
}

/// <summary>Cuentas integradas.</summary>
public static class Accounts
{
    public const string LocalSystem = "LocalSystem";
    public const string LocalService = @"NT AUTHORITY\LocalService";
    public const string NetworkService = @"NT AUTHORITY\NetworkService";

    public static bool IsLocalSystem(string? account) =>
        string.IsNullOrWhiteSpace(account)
        || account.Equals(LocalSystem, StringComparison.OrdinalIgnoreCase)
        || account.Equals(@".\LocalSystem", StringComparison.OrdinalIgnoreCase)
        || account.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);

    /// <summary>Normaliza los alias de las cuentas integradas.</summary>
    public static string Normalize(string? account)
    {
        if (IsLocalSystem(account))
            return LocalSystem;
        var a = account!.Trim();
        if (a.Equals("LocalService", StringComparison.OrdinalIgnoreCase) || a.Equals(@"NT AUTHORITY\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) || a.Equals(LocalService, StringComparison.OrdinalIgnoreCase))
            return LocalService;
        if (a.Equals("NetworkService", StringComparison.OrdinalIgnoreCase) || a.Equals(@"NT AUTHORITY\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase) || a.Equals(NetworkService, StringComparison.OrdinalIgnoreCase))
            return NetworkService;
        return a;
    }

    /// <summary>
    /// Si la cuenta necesita contraseña: no la necesitan las integradas, las virtuales
    /// (<c>NT SERVICE\…</c>) ni las cuentas de servicio administradas (acaban en <c>$</c>).
    /// </summary>
    public static bool NeedsPassword(string? account)
    {
        var a = Normalize(account);
        return !(a == LocalSystem || a == LocalService || a == NetworkService
            || a.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase)
            || a.EndsWith('$'));
    }
}
