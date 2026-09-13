using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for <c>System.Security.Cryptography.ProtectedData</c> (WP7's DPAPI wrapper), which
    /// games use to encrypt their save files.
    ///
    /// <para><b>Both methods used to be stubs that returned null</b>, behind a
    /// "TODO: fix .net 8 compatibility" — the real <c>ProtectedData</c> is Windows-only on .NET
    /// and throws <c>PlatformNotSupportedException</c> on Android, so it had been commented out
    /// and never replaced. Returning null does not degrade, it crashes: a caller does
    /// <c>Protect(...)</c> then <c>ldlen</c> on the result, so the save path dies with a
    /// NullReferenceException. Contre Jour is the reference case —
    /// <c>Default.Namespace.UserData.SaveUserData</c> NRE'd out of the game's
    /// <c>OnDeactivated</c> handler, i.e. the game died the moment it lost focus or was
    /// backgrounded, which on Android is every single time.</para>
    ///
    /// <para><b>The contract that matters is round-tripping, not DPAPI compatibility.</b> Nothing
    /// reads these blobs but the game that wrote them, on this device; a WP7 DPAPI blob could
    /// never have been decrypted here anyway. So this is AES-256-CBC with an encrypt-then-MAC
    /// HMAC-SHA256, which is available identically on both heads, and the only hard requirements
    /// are that <see cref="Unprotect"/> reverses <see cref="Protect"/>, that neither ever returns
    /// null, and that a blob written by one run is readable by the next.</para>
    ///
    /// <para><b>The key is a constant, deliberately — not derived from machine or user identity.</b>
    /// DPAPI's <c>CurrentUser</c> scope exists to stop another user on the same box reading the
    /// blob, and that boundary is already provided here by where the file lives: per-user under
    /// <c>%LocalAppData%\WPR</c> on Windows, per-app private storage on Android. Deriving from
    /// <c>MachineName</c>/<c>UserName</c> would buy nothing and add a real failure mode — if either
    /// ever read back differently the key changes, every existing save fails authentication, and
    /// the games this matters to (see below) have no catch to recover with. Stability wins.</para>
    ///
    /// <para><b>A blob that fails authentication throws <see cref="CryptographicException"/></b>,
    /// which is what real DPAPI does for corrupt input and therefore what a game's error handling
    /// (where it has any) is written against. Note that many do not have any: Contre Jour's
    /// <c>ReadUserData</c> has <c>finally</c> blocks and no <c>catch</c> at all, so a corrupt save
    /// takes the game down. That is faithful behaviour rather than something to paper over — but it
    /// is the reason the key must not drift, and the reason not to change this format casually.
    /// Bump <see cref="FormatVersion"/> and keep reading the old one if you ever do.</para>
    /// </summary>
    public static class ProtectedData
    {
        /// <summary>Identifies a blob as ours, so a foreign one fails fast rather than as a MAC error.</summary>
        private static readonly byte[] Magic = { (byte)'W', (byte)'P', (byte)'R', (byte)'P' };

        private const byte FormatVersion = 1;

        private const int IvLength = 16;    // AES block size
        private const int MacLength = 32;   // HMAC-SHA256
        private const int HeaderLength = 4 + 1 + IvLength;

        /// <summary>
        /// Fixed input to the key derivation. See the note on the class about why this is a
        /// constant. Changing it invalidates every existing save on every installed game.
        /// </summary>
        private const string KeyMaterial = "WPR.WindowsCompability.ProtectedData/v1";

        private static readonly byte[] KeySalt =
            Encoding.UTF8.GetBytes("WPR-ProtectedData-Salt-2026");

        /// <summary>
        /// Encrypts <paramref name="userData"/>. Never returns null.
        /// <paramref name="optionalEntropy"/> is folded into the key, so a blob protected with one
        /// entropy cannot be unprotected with another — the DPAPI behaviour games rely on when they
        /// pass one (most pass null).
        /// </summary>
        public static byte[] Protect(byte[] userData, byte[]? optionalEntropy)
        {
            if (userData == null) throw new ArgumentNullException(nameof(userData));

            byte[] key = DeriveKey(optionalEntropy);

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.GenerateIV();

            byte[] cipher;
            using (ICryptoTransform enc = aes.CreateEncryptor())
            {
                cipher = enc.TransformFinalBlock(userData, 0, userData.Length);
            }

            byte[] result = new byte[HeaderLength + cipher.Length + MacLength];
            Buffer.BlockCopy(Magic, 0, result, 0, Magic.Length);
            result[Magic.Length] = FormatVersion;
            Buffer.BlockCopy(aes.IV, 0, result, Magic.Length + 1, IvLength);
            Buffer.BlockCopy(cipher, 0, result, HeaderLength, cipher.Length);

            // Encrypt-then-MAC over everything that precedes the tag, so a tampered header or IV
            // is caught too.
            using (var hmac = new HMACSHA256(key))
            {
                byte[] mac = hmac.ComputeHash(result, 0, HeaderLength + cipher.Length);
                Buffer.BlockCopy(mac, 0, result, HeaderLength + cipher.Length, MacLength);
            }

            return result;
        }

        /// <summary>
        /// Reverses <see cref="Protect"/>. Never returns null. Throws
        /// <see cref="CryptographicException"/> for anything this did not produce, or that has been
        /// altered, or that was protected with different entropy — see the note on the class.
        /// </summary>
        public static byte[] Unprotect(byte[] encryptedData, byte[]? optionalEntropy)
        {
            if (encryptedData == null) throw new ArgumentNullException(nameof(encryptedData));

            if (encryptedData.Length < HeaderLength + MacLength)
            {
                throw new CryptographicException("Protected blob is too short to be valid.");
            }

            for (int i = 0; i < Magic.Length; i++)
            {
                if (encryptedData[i] != Magic[i])
                {
                    throw new CryptographicException(
                        "Protected blob was not produced by this platform.");
                }
            }

            if (encryptedData[Magic.Length] != FormatVersion)
            {
                throw new CryptographicException(
                    $"Unsupported protected-blob version {encryptedData[Magic.Length]}.");
            }

            byte[] key = DeriveKey(optionalEntropy);
            int cipherLength = encryptedData.Length - HeaderLength - MacLength;

            using (var hmac = new HMACSHA256(key))
            {
                byte[] expected = hmac.ComputeHash(encryptedData, 0, HeaderLength + cipherLength);
                var actual = new byte[MacLength];
                Buffer.BlockCopy(encryptedData, HeaderLength + cipherLength, actual, 0, MacLength);

                // Fixed-time compare: the timing channel is meaningless for a local save file, but
                // the fixed-time helper is right there and this is the shape to copy elsewhere.
                if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                {
                    throw new CryptographicException(
                        "Protected blob failed authentication (wrong entropy, or corrupt).");
                }
            }

            var iv = new byte[IvLength];
            Buffer.BlockCopy(encryptedData, Magic.Length + 1, iv, 0, IvLength);

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;

            using ICryptoTransform dec = aes.CreateDecryptor();
            return dec.TransformFinalBlock(encryptedData, HeaderLength, cipherLength);
        }

        /// <summary>
        /// The no-entropy key, derived once. Nearly every call site passes null entropy, and a
        /// game can save on every level change or deactivation — paying 10k PBKDF2 iterations each
        /// time would be a visible stall for no benefit, since the input is a compile-time constant.
        /// </summary>
        private static readonly Lazy<byte[]> DefaultKey =
            new Lazy<byte[]>(() => DeriveKeyCore(null), isThreadSafe: true);

        private static byte[] DeriveKey(byte[]? optionalEntropy)
        {
            if (optionalEntropy == null || optionalEntropy.Length == 0)
            {
                return DefaultKey.Value;
            }

            return DeriveKeyCore(optionalEntropy);
        }

        private static byte[] DeriveKeyCore(byte[]? optionalEntropy)
        {
            // The entropy has to change the key rather than merely be appended to the plaintext,
            // or Unprotect could not tell "protected with different entropy" from "correct".
            byte[] material = Encoding.UTF8.GetBytes(KeyMaterial);
            if (optionalEntropy != null && optionalEntropy.Length > 0)
            {
                var combined = new byte[material.Length + optionalEntropy.Length];
                Buffer.BlockCopy(material, 0, combined, 0, material.Length);
                Buffer.BlockCopy(optionalEntropy, 0, combined, material.Length, optionalEntropy.Length);
                material = combined;
            }

            using var kdf = new Rfc2898DeriveBytes(material, KeySalt, 10_000, HashAlgorithmName.SHA256);
            return kdf.GetBytes(32);
        }
    }
}
