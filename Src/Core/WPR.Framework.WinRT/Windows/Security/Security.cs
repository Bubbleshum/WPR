using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Storage.Streams;
using WPR.WinRT;

namespace Windows.Security.Credentials
{
    /// <summary>Shim for <c>Windows.Security.Credentials.PasswordCredential</c>.</summary>
    public sealed class PasswordCredential
    {
        public PasswordCredential()
        {
        }

        public PasswordCredential(string resource, string userName, string password)
        {
            Resource = resource;
            UserName = userName;
            Password = password;
        }

        public string Resource { get; private set; } = string.Empty;

        public void put_Resource(string value) => Resource = value;

        public string UserName { get; private set; } = string.Empty;

        public void put_UserName(string value) => UserName = value;

        public string Password { get; private set; } = string.Empty;

        public void put_Password(string value) => Password = value;

        /// <summary>The vault's lookups already fill the password in.</summary>
        public void RetrievePassword()
        {
        }
    }

    /// <summary>
    /// Shim for <c>Windows.Security.Credentials.PasswordVault</c>: per title, kept beside its
    /// local folder (<c>PasswordVault.json</c>) so a sign-in survives a relaunch, as on the phone.
    /// Not encrypted: the file sits in the same per-title store as the title's own saves.
    /// </summary>
    public sealed class PasswordVault
    {
        private static readonly object Lock = new();

        private sealed record Entry(string Resource, string UserName, string Password);

        private static string StorePath => Path.Combine(WinRtHost.LocalFolderPath, "..", "PasswordVault.json");

        private static List<Entry> Load()
        {
            try
            {
                return File.Exists(StorePath)
                    ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(StorePath)) ?? new()
                    : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new();
            }
        }

        private static void Save(List<Entry> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(StorePath))!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(entries));
        }

        private static Exception NotFound() =>
            new Exception("Element not found.") { HResult = unchecked((int)0x80070490) };

        public void Add(PasswordCredential credential)
        {
            lock (Lock)
            {
                List<Entry> all = Load();
                all.RemoveAll(e => Same(e, credential.Resource, credential.UserName));
                all.Add(new Entry(credential.Resource, credential.UserName, credential.Password));
                Save(all);
            }
        }

        public void Remove(PasswordCredential credential)
        {
            lock (Lock)
            {
                List<Entry> all = Load();
                if (all.RemoveAll(e => Same(e, credential.Resource, credential.UserName)) == 0) throw NotFound();
                Save(all);
            }
        }

        public PasswordCredential Retrieve(string resource, string userName)
        {
            lock (Lock)
            {
                Entry e = Load().FirstOrDefault(x => Same(x, resource, userName)) ?? throw NotFound();
                return new PasswordCredential(e.Resource, e.UserName, e.Password);
            }
        }

        public IReadOnlyList<PasswordCredential> FindAllByResource(string resource) => Find(e => Eq(e.Resource, resource));

        public IReadOnlyList<PasswordCredential> FindAllByUserName(string userName) => Find(e => Eq(e.UserName, userName));

        public IReadOnlyList<PasswordCredential> RetrieveAll()
        {
            lock (Lock) return Load().Select(e => new PasswordCredential(e.Resource, e.UserName, e.Password)).ToList();
        }

        /// <summary>WinRT throws when nothing matches rather than answering an empty list.</summary>
        private IReadOnlyList<PasswordCredential> Find(Func<Entry, bool> match)
        {
            lock (Lock)
            {
                List<PasswordCredential> found = Load().Where(match).Select(e => new PasswordCredential(e.Resource, e.UserName, e.Password)).ToList();
                return found.Count > 0 ? found : throw NotFound();
            }
        }

        private static bool Same(Entry e, string resource, string userName) => Eq(e.Resource, resource) && Eq(e.UserName, userName);

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

namespace Windows.Security.Cryptography
{
    /// <summary>Shim for <c>Windows.Security.Cryptography.BinaryStringEncoding</c>.</summary>
    public enum BinaryStringEncoding
    {
        Utf8 = 0,
        Utf16LE = 1,
        Utf16BE = 2,
    }

    /// <summary>Shim for <c>Windows.Security.Cryptography.CryptographicBuffer</c>.</summary>
    public static class CryptographicBuffer
    {
        private static Encoding For(BinaryStringEncoding encoding) => encoding switch
        {
            BinaryStringEncoding.Utf16LE => Encoding.Unicode,
            BinaryStringEncoding.Utf16BE => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };

        public static IBuffer ConvertStringToBinary(string value, BinaryStringEncoding encoding) => For(encoding).GetBytes(value ?? string.Empty).AsBuffer();

        public static string ConvertBinaryToString(BinaryStringEncoding encoding, IBuffer buffer) => For(encoding).GetString(buffer.ToArray());

        public static string EncodeToBase64String(IBuffer buffer) => Convert.ToBase64String(buffer.ToArray());

        public static IBuffer DecodeFromBase64String(string value) => Convert.FromBase64String(value).AsBuffer();

        public static string EncodeToHexString(IBuffer buffer) => Convert.ToHexString(buffer.ToArray()).ToLowerInvariant();

        public static IBuffer DecodeFromHexString(string value) => Convert.FromHexString(value).AsBuffer();

        public static IBuffer CreateFromByteArray(byte[] value) => ((byte[])value.Clone()).AsBuffer();

        public static void CopyToByteArray(IBuffer buffer, out byte[] value) => value = buffer.ToArray();

        public static IBuffer GenerateRandom(uint length) => RandomNumberGenerator.GetBytes((int)length).AsBuffer();

        public static uint GenerateRandomNumber() => BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));

        public static bool Compare(IBuffer object1, IBuffer object2) => object1.ToArray().AsSpan().SequenceEqual(object2.ToArray());
    }
}
