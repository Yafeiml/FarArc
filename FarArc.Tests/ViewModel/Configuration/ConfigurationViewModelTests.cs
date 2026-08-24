using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using FarArc.Service;

namespace FarArc.Tests.ViewModel.Configuration
{
    [TestClass]
    [DoNotParallelize]
    public sealed class ConfigurationViewModelTests
    {
        private string _testDirectory = null!;
        private AppPathHelper _previousAppPath = null!;

        [TestInitialize]
        public void Initialize()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "FarArc.Tests", Guid.NewGuid().ToString("N"));
            _previousAppPath = TestInit.UseIsolatedAppPath(_testDirectory);
        }

        [TestCleanup]
        public void Cleanup()
        {
            AppPathHelper.Instance = _previousAppPath;
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }

        [TestMethod]
        public void ConfigurationService_ClampsAndPersistsCurrentSettings()
        {
            var configuration = new FarArc.Service.Configuration
            {
                DatabaseCheckPeriod = 150,
                DatabaseReconnectPeriod = -5
            };
            var service = new ConfigurationService(new KeywordMatchService(), configuration);

            Assert.AreEqual(99, service.DatabaseCheckPeriod);
            Assert.AreEqual(0, service.DatabaseReconnectPeriod);

            service.DatabaseCheckPeriod = 42;
            service.DatabaseReconnectPeriod = 180;
            service.General.ConfirmBeforeClosingSession = true;
            service.Save();

            Assert.IsTrue(File.Exists(AppPathHelper.Instance.ProfileJsonPath));
            var persisted = JsonConvert.DeserializeObject<FarArc.Service.Configuration>(
                File.ReadAllText(AppPathHelper.Instance.ProfileJsonPath));

            Assert.IsNotNull(persisted);
            Assert.AreEqual(42, persisted.DatabaseCheckPeriod);
            Assert.AreEqual(180, persisted.DatabaseReconnectPeriod);
            Assert.IsTrue(persisted.General.ConfirmBeforeClosingSession);
        }
    }
}
