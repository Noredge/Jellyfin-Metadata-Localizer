using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Localizer.Plugin;

// Reads the same CurrentUser DPAPI hex format produced by PowerShell ConvertFrom-SecureString.
// Fixed server-owned location, never a path or secret supplied through the web API.
internal static class ProviderCredential
{
    internal static string FilePath(string root, string serviceId)
    {
        if (!Localizer.Translation.TranslationServiceProfile.ValidId(serviceId)) throw new ArgumentException("InvalidServiceIdentity");
        return Path.Combine(root, "credentials", "service-" + serviceId.ToLowerInvariant() + "-api-key.dpapi");
    }
    internal static string Load(string root, string serviceId, string endpoint)
    {
        new ServiceSettingsStore(root).RequireCredentialEndpoint(serviceId, endpoint);
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("WindowsCredentialRequired");
        var file = FilePath(root, serviceId);
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
                if (string.IsNullOrWhiteSpace(key) || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || key.Length > 4096)
                    throw new InvalidOperationException("InvalidCredential");
                // Close a configuration-change race while a newly created key was being read.
                new ServiceSettingsStore(root).RequireCredentialEndpoint(serviceId, endpoint);
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
    internal static string LoadFor(string root, Localizer.Translation.TranslationServiceProfile profile) => profile.Kind switch
    {
        "openai" => OpenAiCredential.Load(root),
        "groq" => GroqCredential.Load(root),
        "openai-compatible" => Load(root, profile.Id, profile.CanonicalEndpoint()),
        _ => throw new InvalidOperationException("NoCloudCredentialForService")
    };
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
