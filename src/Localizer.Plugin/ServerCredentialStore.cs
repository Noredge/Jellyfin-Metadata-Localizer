using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Localizer.Plugin;

// Endpoint-bound CurrentUser DPAPI. The web request key is never serialized into settings or returned.
internal sealed class ServerCredentialStore(string root)
{
    private sealed record Envelope(int SchemaVersion, string Endpoint, string ProtectedHex);
    internal static void ValidateKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("InvalidCredential");
    }
    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("InvalidCredentialReference");
        return Path.Combine(root, "credentials", "compatible", id + ".json");
    }
    internal string Save(string endpoint, string value)
    {
        ValidateKey(value);
        var clear = Encoding.UTF8.GetBytes(value);
        byte[] encrypted;
        try { encrypted = Transform(clear, endpoint, protect: true); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        var id = Guid.NewGuid().ToString("N"); var path = PathFor(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, new Envelope(1, endpoint, Convert.ToHexString(encrypted))); stream.Flush(true);
        return id;
    }
    internal string Load(string endpoint, string reference)
    {
        var path = PathFor(reference);
        if (new FileInfo(path).Length > 65536) throw new InvalidOperationException("InvalidCredential");
        Envelope value;
        try { value = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(path)) ?? throw new InvalidOperationException("InvalidCredential"); }
        catch (JsonException) { throw new InvalidOperationException("InvalidCredential"); }
        if (value.SchemaVersion != 1 || value.Endpoint != endpoint) throw new InvalidOperationException("CredentialEndpointMismatch");
        var bytes = Transform(Convert.FromHexString(value.ProtectedHex), endpoint, protect: false);
        try { var key = Encoding.UTF8.GetString(bytes); ValidateKey(key); return key; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] Transform(byte[] value, string endpoint, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("WindowsCredentialRequired");
        var entropy = Encoding.UTF8.GetBytes("MetadataLocalizer/compatible/v1/" + endpoint);
        var input = new Blob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var salt = new Blob { Size = entropy.Length, Data = Marshal.AllocHGlobal(entropy.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length); Marshal.Copy(entropy, 0, salt.Data, entropy.Length);
            var ok = protect ? CryptProtectData(ref input, null, ref salt, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("CredentialUnavailableForServiceIdentity");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (var i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(salt.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description,
        ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description,
        ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
