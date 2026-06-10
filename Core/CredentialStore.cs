using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RGDSCapture.Core
{
    /// <summary>
    /// Encrypts the saved SSH password with Windows DPAPI (CurrentUser scope)
    /// via direct crypt32 P/Invoke — no extra package. The ciphertext is only
    /// decryptable by the same Windows user on the same machine, which is the
    /// right threat model for a settings.json sitting in %APPDATA%.
    /// </summary>
    public static class CredentialStore
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("RGDSCapture.credential.v1");

        /// <summary>Returns base64 ciphertext, or null if encryption failed.</summary>
        public static string? Protect(string secret)
        {
            try
            {
                byte[] cipher = Transform(Encoding.UTF8.GetBytes(secret), protect: true);
                return Convert.ToBase64String(cipher);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Returns the plaintext, or null if the blob is invalid for this user/machine.</summary>
        public static string? Unprotect(string base64)
        {
            try
            {
                byte[] plain = Transform(Convert.FromBase64String(base64), protect: false);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }

        // ── DPAPI interop ─────────────────────────────────────────────
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static byte[] Transform(byte[] input, bool protect)
        {
            IntPtr inPtr = Marshal.AllocHGlobal(input.Length);
            IntPtr entropyPtr = Marshal.AllocHGlobal(Entropy.Length);
            var outBlob = default(DATA_BLOB);
            try
            {
                Marshal.Copy(input, 0, inPtr, input.Length);
                Marshal.Copy(Entropy, 0, entropyPtr, Entropy.Length);

                var inBlob = new DATA_BLOB { cbData = input.Length, pbData = inPtr };
                var entropyBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = entropyPtr };

                bool ok = protect
                    ? CryptProtectData(ref inBlob, null, ref entropyBlob,
                        IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob,
                        IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);

                if (!ok || outBlob.pbData == IntPtr.Zero)
                    throw new InvalidOperationException("DPAPI transform failed.");

                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(inPtr);
                Marshal.FreeHGlobal(entropyPtr);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
    }
}
