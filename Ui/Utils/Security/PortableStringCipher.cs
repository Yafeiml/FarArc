using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace _1RM.Utils.Security
{
    /// <summary>
    /// Encrypts portable, versioned string payloads with AES-256-GCM.
    /// Ciphertext is not tied to a Windows user or device: another client can
    /// decrypt it when it is configured with the same key material.
    /// </summary>
    public sealed class PortableStringCipher : IDisposable
    {
        private const string FormatName = "rmsec";
        private const string FormatVersion = "1";
        private const int KeySizeInBytes = 32;
        private const int NonceSizeInBytes = 12;
        private const int TagSizeInBytes = 16;

        private static readonly byte[] DerivationSalt = SHA256.HashData(
            Encoding.UTF8.GetBytes("RemoteManager.PortableStringCipher.HKDF-SHA256/v1"));
        private static readonly byte[] DerivationInfo =
            Encoding.UTF8.GetBytes("RemoteManager.PortableStringCipher.AES-256-GCM/v1");

        private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
        private readonly byte[] _primaryKey;
        private bool _disposed;

        public PortableStringCipher(string primaryKeyMaterial, params string[] decryptionKeyMaterials)
        {
            _primaryKey = AddKey(primaryKeyMaterial);
            PrimaryKeyId = GetKeyId(_primaryKey);

            try
            {
                foreach (var keyMaterial in decryptionKeyMaterials ?? [])
                {
                    AddKey(keyMaterial);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string PrimaryKeyId { get; }

        public static bool IsCipherText(string? value)
        {
            return value?.StartsWith($"{FormatName}:{FormatVersion}:", StringComparison.Ordinal) == true;
        }

        public string Encrypt(string plainText)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(plainText);

            var nonce = RandomNumberGenerator.GetBytes(NonceSizeInBytes);
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            var cipherTextBytes = new byte[plainTextBytes.Length];
            var tag = new byte[TagSizeInBytes];
            var header = GetAuthenticatedHeader(PrimaryKeyId);

            try
            {
                using var aes = new AesGcm(_primaryKey, TagSizeInBytes);
                aes.Encrypt(nonce, plainTextBytes, cipherTextBytes, tag, header);

                return string.Join(
                    ':',
                    FormatName,
                    FormatVersion,
                    PrimaryKeyId,
                    Convert.ToBase64String(nonce),
                    Convert.ToBase64String(cipherTextBytes),
                    Convert.ToBase64String(tag));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainTextBytes);
            }
        }

        public bool TryDecrypt(string cipherText, out string plainText)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            plainText = "";

            if (!TryParse(cipherText, out var keyId, out var nonce, out var encryptedBytes, out var tag))
                return false;

            if (!_keys.TryGetValue(keyId, out var key))
                return false;

            var plainTextBytes = new byte[encryptedBytes.Length];
            var header = GetAuthenticatedHeader(keyId);

            try
            {
                using var aes = new AesGcm(key, TagSizeInBytes);
                aes.Decrypt(nonce, encryptedBytes, tag, plainTextBytes, header);
                plainText = Encoding.UTF8.GetString(plainTextBytes);
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainTextBytes);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            foreach (var key in _keys.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            _keys.Clear();
            _disposed = true;
        }

        private byte[] AddKey(string keyMaterial)
        {
            if (string.IsNullOrWhiteSpace(keyMaterial))
                throw new ArgumentException("Encryption key material cannot be empty.", nameof(keyMaterial));

            var key = DeriveKey(keyMaterial);
            var keyId = GetKeyId(key);

            if (_keys.TryGetValue(keyId, out var existingKey))
            {
                if (!CryptographicOperations.FixedTimeEquals(existingKey, key))
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new CryptographicException("Two different encryption keys produced the same key identifier.");
                }

                CryptographicOperations.ZeroMemory(key);
                return existingKey;
            }

            _keys.Add(keyId, key);
            return key;
        }

        private static byte[] DeriveKey(string keyMaterial)
        {
            var inputKeyMaterial = Encoding.UTF8.GetBytes(keyMaterial);
            byte[]? pseudoRandomKey = null;
            byte[]? expandInput = null;

            try
            {
                using (var extract = new HMACSHA256(DerivationSalt))
                {
                    pseudoRandomKey = extract.ComputeHash(inputKeyMaterial);
                }

                expandInput = new byte[DerivationInfo.Length + 1];
                DerivationInfo.CopyTo(expandInput, 0);
                expandInput[^1] = 0x01;

                using var expand = new HMACSHA256(pseudoRandomKey);
                var key = expand.ComputeHash(expandInput);
                if (key.Length != KeySizeInBytes)
                    throw new CryptographicException("The encryption key derivation returned an unexpected length.");

                return key;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(inputKeyMaterial);
                if (pseudoRandomKey != null)
                    CryptographicOperations.ZeroMemory(pseudoRandomKey);
                if (expandInput != null)
                    CryptographicOperations.ZeroMemory(expandInput);
            }
        }

        private static string GetKeyId(byte[] key)
        {
            var fingerprint = SHA256.HashData(key);
            try
            {
                return Convert.ToHexString(fingerprint.AsSpan(0, 16)).ToLowerInvariant();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fingerprint);
            }
        }

        private static byte[] GetAuthenticatedHeader(string keyId)
        {
            return Encoding.UTF8.GetBytes($"{FormatName}:{FormatVersion}:{keyId}");
        }

        private static bool TryParse(
            string cipherText,
            out string keyId,
            out byte[] nonce,
            out byte[] encryptedBytes,
            out byte[] tag)
        {
            keyId = "";
            nonce = [];
            encryptedBytes = [];
            tag = [];

            if (!IsCipherText(cipherText))
                return false;

            var parts = cipherText.Split(':');
            if (parts.Length != 6
                || parts[0] != FormatName
                || parts[1] != FormatVersion
                || parts[2].Length != 32
                || parts[2].Any(c => !Uri.IsHexDigit(c)))
            {
                return false;
            }

            try
            {
                keyId = parts[2];
                nonce = Convert.FromBase64String(parts[3]);
                encryptedBytes = Convert.FromBase64String(parts[4]);
                tag = Convert.FromBase64String(parts[5]);
                return nonce.Length == NonceSizeInBytes && tag.Length == TagSizeInBytes;
            }
            catch (FormatException)
            {
                keyId = "";
                nonce = [];
                encryptedBytes = [];
                tag = [];
                return false;
            }
        }
    }
}
