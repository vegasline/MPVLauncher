using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Stable Chromium unpacked/packed extension ID derived from a persisted RSA public key.
    /// </summary>
    public static class ChromiumExtensionKey
    {
        public static (string Id, string PublicKeyBase64) GetOrCreate()
        {
            AppPaths.EnsureLayout();
            RSA rsa = RSA.Create(2048);
            try
            {
                if (File.Exists(AppPaths.ChromiumKeyFile))
                {
                    rsa.ImportFromPem(File.ReadAllText(AppPaths.ChromiumKeyFile));
                }
                else
                {
                    File.WriteAllText(AppPaths.ChromiumKeyFile, rsa.ExportPkcs8PrivateKeyPem());
                }

                byte[] spki = rsa.ExportSubjectPublicKeyInfo();
                string id = ToChromeId(spki);
                string b64 = Convert.ToBase64String(spki);
                return (id, b64);
            }
            finally
            {
                rsa.Dispose();
            }
        }

        /// <summary>
        /// Chrome unpacked ID when no manifest key is present: SHA256 of the lowercase UTF-16 path.
        /// </summary>
        public static string FromUnpackedPath(string directory)
        {
            string pathLower = Path.GetFullPath(directory).ToLowerInvariant();
            byte[] bytes = Encoding.Unicode.GetBytes(pathLower);
            byte[] hash = SHA256.HashData(bytes);
            return ToChromeId(hash, alreadyHashed: true);
        }

        private static string ToChromeId(byte[] data, bool alreadyHashed = false)
        {
            byte[] hash = alreadyHashed ? data : SHA256.HashData(data);
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++)
            {
                sb.Append((char)((hash[i] >> 4) + 'a'));
                sb.Append((char)((hash[i] & 0x0F) + 'a'));
            }
            return sb.ToString();
        }
    }
}
