using System.Runtime.InteropServices;

namespace SocWsManager.Scm;

/// <summary>
/// El derecho «Iniciar sesión como servicio» (<c>SeServiceLogonRight</c>): sin él, una cuenta de
/// usuario no puede ser la de un servicio. Se concede al darla de alta con contraseña (RF-54).
/// Necesita administrador.
/// </summary>
public static unsafe partial class LogonRight
{
    public const string ServiceLogon = "SeServiceLogonRight";

    /// <summary>Concede el derecho si no lo tenía. Devuelve true si lo ha añadido ahora.</summary>
    public static bool Grant(string account)
    {
        var sid = Sid(account);
        fixed (byte* pSid = sid)
        {
            var policy = Open();
            try
            {
                if (Has(policy, pSid))
                    return false;
                fixed (char* right = ServiceLogon)
                {
                    var str = new LsaUnicodeString { Length = (ushort)(ServiceLogon.Length * 2), MaximumLength = (ushort)(ServiceLogon.Length * 2), Buffer = right };
                    var status = LsaAddAccountRights(policy, pSid, &str, 1);
                    if (status != 0)
                        throw new ScmException((int)LsaNtStatusToWinError(status), "LsaAddAccountRights");
                }
                return true;
            }
            finally
            {
                LsaClose(policy);
            }
        }
    }

    private static bool Has(nint policy, byte* sid)
    {
        if (LsaEnumerateAccountRights(policy, sid, out var rights, out var count) != 0)
            return false;   // sin derechos aún: STATUS_OBJECT_NAME_NOT_FOUND
        try
        {
            var list = (LsaUnicodeString*)rights;
            for (var i = 0; i < count; i++)
                if (new string(list[i].Buffer, 0, list[i].Length / 2).Equals(ServiceLogon, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        finally
        {
            LsaFreeMemory(rights);
        }
    }

    private static nint Open()
    {
        var attributes = default(LsaObjectAttributes);
        attributes.Length = sizeof(LsaObjectAttributes);
        var status = LsaOpenPolicy(null, &attributes, 0x00000010 | 0x00000800 /* POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES */, out var policy);
        if (status != 0)
            throw new ScmException((int)LsaNtStatusToWinError(status), "LsaOpenPolicy");
        return policy;
    }

    /// <summary>El SID de la cuenta (<c>.\usuario</c> se entiende como del equipo).</summary>
    public static byte[] Sid(string account)
    {
        var name = account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account[1..] : account;
        uint sidSize = 0, domainSize = 0;
        LookupAccountNameW(null, name, null, ref sidSize, null, ref domainSize, out _);
        if (sidSize == 0)
            throw new ScmException(Marshal.GetLastPInvokeError(), "LookupAccountName");
        var sid = new byte[sidSize];
        var domain = new char[domainSize];
        fixed (byte* s = sid)
        fixed (char* d = domain)
        {
            if (!LookupAccountNameW(null, name, s, ref sidSize, d, ref domainSize, out _))
                throw new ScmException(Marshal.GetLastPInvokeError(), "LookupAccountName");
        }
        return sid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaOpenPolicy(LsaUnicodeString* system, LsaObjectAttributes* attributes, uint access, out nint policy);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaAddAccountRights(nint policy, byte* sid, LsaUnicodeString* rights, uint count);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaEnumerateAccountRights(nint policy, byte* sid, out nint rights, out uint count);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaFreeMemory(nint buffer);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaClose(nint policy);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaNtStatusToWinError(uint status);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupAccountNameW(string? system, string account, byte* sid, ref uint sidSize, char* domain, ref uint domainSize, out int use);
}
