using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shawn.Utils;
using static Shawn.Utils.VersionHelper;

namespace Tests.Utils
{
    [TestClass]
    public class VersionHelperTests
    {
        [TestMethod]
        public void FromStringTest()
        {
            var v1 = new Version(0, 6, 1, 0);
            var v2 = Version.FromString(v1.ToString());
            Assert.IsTrue(v1 == v2);
        }

        [TestMethod]
        public void CompareTest()
        {
            var v1 = new Version(0, 6, 1, 0);
            var v2 = new Version(0, 6, 1, 0);
            var v3 = new Version(0, 6, 1, 1);
            var v4 = new Version(0, 6, 2, 0);
            var v5 = new Version(0, 7, 1, 0);
            var v6 = new Version(1, 6, 1, 0);
            var v7 = new Version(0, 6, 1, 0, "alpha");
            var v8 = new Version(0, 6, 1, 0, "beta");
            var v9 = new Version(0, 6, 1, 0, "beta2");
            Assert.IsTrue(v1 == v2);
            Assert.IsTrue(v1 >= v2);
            Assert.IsTrue(v3 > v2);
            Assert.IsTrue(v3 != v2);
            Assert.IsTrue(v2 < v3);
            Assert.IsTrue(v3 >= v2);
            Assert.IsTrue(v4 > v3);
            Assert.IsTrue(v3 < v4);
            Assert.IsTrue(v3 <= v4);
            Assert.IsTrue(v5 > v4);
            Assert.IsTrue(v6 > v5);
            Assert.IsTrue(v6 > v7);
            Assert.IsTrue(v8 > v7);
            Assert.IsTrue(v9 > v8);
            Assert.IsTrue(v1 > v9);
            Assert.IsTrue(v9 != v8);
            Assert.IsTrue(Version.Compare(v1, v3));
            Assert.IsTrue(Version.Compare(v9, v1));
        }

        [TestMethod]
        public void DefaultCheckMethodTest()
        {
            var v1 = new Version(0, 6, 1, 0);
            var v2 = new Version(0, 6, 2, 0);
            var v3 = new Version(0, 7, 1, 0);
            const string url = "www.xxxx.xx";

            var update = VersionHelper.DefaultCheckMethod($"latest version: {v2}", url, v1, null);
            Assert.IsTrue(update.NewerPublished);
            Assert.IsTrue(Version.FromString(update.NewerVersion) == v2);
            Assert.AreEqual(url, update.NewerUrl);
            Assert.IsFalse(update.NewerHasBreakChange);

            var ignoredByNewerVersion = VersionHelper.DefaultCheckMethod($"latest version: {v2}", url, v1, v3);
            Assert.IsFalse(ignoredByNewerVersion.NewerPublished);

            var ignoredBySameVersion = VersionHelper.DefaultCheckMethod($"latest version: {v2}", url, v1, v2);
            Assert.IsFalse(ignoredBySameVersion.NewerPublished);

            var newerThanIgnored = VersionHelper.DefaultCheckMethod($"latest version: {v3}", url, v1, v2);
            Assert.IsTrue(newerThanIgnored.NewerPublished);
            Assert.AreEqual(v3.ToString(), newerThanIgnored.NewerVersion);

            var breakingUpdate = VersionHelper.DefaultCheckMethod($"!latest version: {v2}!", url, v1, null);
            Assert.IsTrue(breakingUpdate.NewerPublished);
            Assert.IsTrue(breakingUpdate.NewerHasBreakChange);
        }
    }
}
