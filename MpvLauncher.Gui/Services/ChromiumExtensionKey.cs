using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Owns the Chromium extension identity.
    ///
    /// A Chromium extension's ID is the first 32 characters of a SHA-256 hash of
    /// its public key, mapped onto the letters a-p. For a loaded unpacked
    /// extension that key lives in manifest.json, so the same unpacked folder
    /// keeps the same ID across restarts only if the key does too. Persisting one
    /// RSA key pair in %APPDATA% is what makes the ID, and therefore the
    /// browser's per-extension grants, survive a reinstall or a path change.
    ///
    /// The private key is stored as an unencrypted PKCS#8 PEM. It never leaves
    /// this machine and the file inherits the per-user ACL of %APPDATA%, but it
    /// is still a signing key: anyone who can read it can produce an extension
    /// with this ID. Deleting the file is safe - it simply mints a new ID and
    /// the browser will ask for native-messaging permission again.
    /// </summary>
    public static class ChromiumExtensionKey
    {
        /// <summary>
        /// Loads the persisted key, generating and saving one on first run.
        /// </summary>
        /// <returns>
        /// The extension ID, and the same public key as base64 DER for the
        /// manifest's "key" field.
        /// </returns>
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
                    // No key yet: create one so this install gets a permanent ID.
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
        /// The ID Chromium assigns to an unpacked folder that has no manifest key:
        /// SHA-256 over the lowercase UTF-16 absolute path. Only used as a
        /// diagnostic to explain why an ID does not match the persisted one.
        /// </summary>
        public static string FromUnpackedPath(string directory)
        {
            string pathLower = Path.GetFullPath(directory).ToLowerInvariant();
            byte[] bytes = Encoding.Unicode.GetBytes(pathLower);
            byte[] hash = SHA256.HashData(bytes);
            return ToChromeId(hash, alreadyHashed: true);
        }

        /// <summary>
        /// Maps 16 bytes of a hash onto Chromium's 32-character ID alphabet,
        /// which is 'a' through 'p' - one hex digit would have needed 32 symbols
        /// too, and this is the mapping Chromium itself uses.
        /// </summary>
        private static string ToChromeId(byte[] data, bool alreadyHashed = false)
        {
            byte[] hash = alreadyHashed ? data : SHA256.HashData(data);
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++)
            {
                sb.Append((char)((hash[i] >> 4) + 'a'));   // high nibble
                sb.Append((char)((hash[i] & 0x0F) + 'a')); // low nibble
            }
            return sb.ToString();
        }
    }
}
