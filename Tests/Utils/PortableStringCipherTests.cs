using System;
using _1RM.Utils;
using _1RM.Utils.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.Utils
{
    [TestClass]
    public sealed class PortableStringCipherTests
    {
        private const string PrimaryKey = "Q3J5cHRvZ3JhcGhpY2FsbHktcmFuZG9tLXRlc3Qta2V5LTE=";
        private const string OtherKey = "QW5vdGhlci1jcnlwdG9ncmFwaGljLXRlc3Qta2V5LTI=";

        [TestMethod]
        public void EncryptAndDecrypt_RoundTripsUnicodeAndEmptyStrings()
        {
            using var cipher = new PortableStringCipher(PrimaryKey);

            foreach (var plainText in new[] { "", "hello", "密码🔐\r\nsecond line" })
            {
                var encrypted = cipher.Encrypt(plainText);

                Assert.IsTrue(PortableStringCipher.IsCipherText(encrypted));
                Assert.IsTrue(cipher.TryDecrypt(encrypted, out var decrypted));
                Assert.AreEqual(plainText, decrypted);
            }
        }

        [TestMethod]
        public void Encrypt_UsesANewNonceForEveryValue()
        {
            using var cipher = new PortableStringCipher(PrimaryKey);

            var first = cipher.Encrypt("same value");
            var second = cipher.Encrypt("same value");

            Assert.AreNotEqual(first, second);
        }

        [TestMethod]
        public void AnotherDeviceWithTheSameKey_CanDecrypt()
        {
            using var firstDevice = new PortableStringCipher(PrimaryKey);
            using var secondDevice = new PortableStringCipher(PrimaryKey);

            var encrypted = firstDevice.Encrypt("portable secret");

            Assert.AreEqual(firstDevice.PrimaryKeyId, secondDevice.PrimaryKeyId);
            Assert.IsTrue(secondDevice.TryDecrypt(encrypted, out var decrypted));
            Assert.AreEqual("portable secret", decrypted);
        }

        [TestMethod]
        public void WrongKey_CannotDecrypt()
        {
            using var writer = new PortableStringCipher(PrimaryKey);
            using var reader = new PortableStringCipher(OtherKey);

            var encrypted = writer.Encrypt("secret");

            Assert.IsFalse(reader.TryDecrypt(encrypted, out _));
        }

        [TestMethod]
        public void ModifiedCipherText_FailsAuthentication()
        {
            using var cipher = new PortableStringCipher(PrimaryKey);
            var encrypted = cipher.Encrypt("secret");
            var parts = encrypted.Split(':');
            var tag = Convert.FromBase64String(parts[5]);
            tag[0] ^= 0x01;
            parts[5] = Convert.ToBase64String(tag);
            var modified = string.Join(':', parts);

            Assert.IsFalse(cipher.TryDecrypt(modified, out _));
        }

        [TestMethod]
        public void KeyRotation_CanReadOldValuesAndWritesWithTheNewKey()
        {
            using var oldCipher = new PortableStringCipher(PrimaryKey);
            var oldValue = oldCipher.Encrypt("old secret");

            using var rotatedCipher = new PortableStringCipher(OtherKey, PrimaryKey);

            Assert.IsTrue(rotatedCipher.TryDecrypt(oldValue, out var decrypted));
            Assert.AreEqual("old secret", decrypted);

            var newValue = rotatedCipher.Encrypt("new secret");
            StringAssert.Contains(newValue, $":{rotatedCipher.PrimaryKeyId}:");
            Assert.IsFalse(oldCipher.TryDecrypt(newValue, out _));
        }

        [TestMethod]
        public void PlainTextAndMalformedValues_AreNotAcceptedAsCipherText()
        {
            using var cipher = new PortableStringCipher(PrimaryKey);

            Assert.IsFalse(PortableStringCipher.IsCipherText("plain text"));
            Assert.IsFalse(cipher.TryDecrypt("plain text", out _));
            Assert.IsFalse(cipher.TryDecrypt("rmsec:1:not-a-key:bad:bad:bad", out _));
        }

        [TestMethod]
        public void CompatibilityWrapper_DoesNotDoubleEncryptNewCipherText()
        {
            var encrypted = UnSafeStringEncipher.EncryptOnce("wrapper secret");

            Assert.AreEqual(encrypted, UnSafeStringEncipher.EncryptOnce(encrypted));
            Assert.AreEqual("wrapper secret", UnSafeStringEncipher.SimpleDecrypt(encrypted));
            Assert.AreEqual("historical plain text", UnSafeStringEncipher.DecryptOrReturnOriginalString("historical plain text"));
        }
    }
}
