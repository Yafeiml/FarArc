using System;
using _1RM.Utils.Security;

namespace _1RM.Utils
{
    public static class UnSafeStringEncipher
    {
        private static readonly object SyncRoot = new();
        private static PortableStringCipher? _cipher;

        public static void Init(string keyMaterial)
        {
            var candidate = new PortableStringCipher(keyMaterial);

            lock (SyncRoot)
            {
                if (_cipher == null)
                {
                    _cipher = candidate;
                    return;
                }

                if (_cipher.PrimaryKeyId == candidate.PrimaryKeyId)
                {
                    candidate.Dispose();
                    return;
                }
            }

            candidate.Dispose();
            throw new InvalidOperationException("String encryption was already initialized with different key material.");
        }

        public static string SimpleEncrypt(string txt)
        {
            return Cipher.Encrypt(txt);
        }

        public static string? SimpleDecrypt(string encryptString)
        {
            return Cipher.TryDecrypt(encryptString, out var plainText) ? plainText : null;
        }

        public static string EncryptOnce(string str)
        {
            return PortableStringCipher.IsCipherText(str) ? str : SimpleEncrypt(str);
        }

        public static string DecryptOrReturnOriginalString(string originalString)
        {
            return SimpleDecrypt(originalString) ?? originalString;
        }

        private static PortableStringCipher Cipher
        {
            get
            {
                if (_cipher == null)
                    Init(Assert.STRING_ENCRYPTION_KEY);

                return _cipher!;
            }
        }
    }
}
