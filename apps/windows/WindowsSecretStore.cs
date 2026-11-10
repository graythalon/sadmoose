using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Drift.Windows;

internal static class WindowsSecretStore
{
    private static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Drift", "pairing.dpapi");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(PathName)) return null;
            return Encoding.UTF8.GetString(Transform(File.ReadAllBytes(PathName), protect: false));
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
    }

    public static void Save(string value)
    {
        var protectedBytes = Transform(Encoding.UTF8.GetBytes(value), protect: true);
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllBytes(PathName, protectedBytes);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPtr = Marshal.AllocHGlobal(input.Length);
        Marshal.Copy(input, 0, inputPtr, input.Length);
        var inputBlob = new DataBlob { Size = input.Length, Data = inputPtr };
        DataBlob outputBlob;
        try
        {
            var ok = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var output = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, output, 0, outputBlob.Size);
            LocalFree(outputBlob.Data);
            return output;
        }
        finally { Marshal.FreeHGlobal(inputPtr); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
