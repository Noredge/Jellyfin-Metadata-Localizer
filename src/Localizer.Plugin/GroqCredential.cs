using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Localizer.Plugin;

// Reads the same CurrentUser DPAPI hex format produced by PowerShell ConvertFrom-SecureString.
// Fixed server-owned location, never a path or secret supplied through the web API.
internal static class GroqCredential
{
    internal static string FilePath(string root) => Path.Combine(root, "credentials", "groq-api-key.dpapi");
    internal static string Load(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("WindowsCredentialRequired");
        var file = FilePath(root);
        if (new FileInfo(file).Length > 32768) throw new InvalidOperationException("InvalidCredential");
        var encrypted = Convert.FromHexString(File.ReadAllText(file).Trim());
        var input = new Blob { Size = encrypted.Length, Data = Marshal.AllocHGlobal(encrypted.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(encrypted, 0, input.Data, encrypted.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
                throw new InvalidOperationException("CredentialUnavailableForServiceIdentity");
            var clear = new byte[output.Size];
            try
            {
                Marshal.Copy(output.Data, clear, 0, clear.Length);
                var key = Encoding.Unicode.GetString(clear);
                if (!key.StartsWith("gsk_", StringComparison.Ordinal) || key.Any(char.IsWhiteSpace) || key.Length > 4096)
                    throw new InvalidOperationException("InvalidCredential");
                return key;
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
