using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using _1RM.Model.Protocol;
using _1RM.Model.Protocol.Base;
using _1RM.Service;
using _1RM.Service.DataSource.DAO;
using _1RM.Service.DataSource.Model;

namespace Tests.Service
{
    [TestClass]
    [DoNotParallelize]
    public sealed class DataServiceTests
    {
        private string _testDirectory = null!;
        private SqliteSource _dataSource = null!;

        [TestInitialize]
        public void Initialize()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "1Remote.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDirectory);

            _dataSource = new SqliteSource("Tests")
            {
                Path = Path.Combine(_testDirectory, "test.db")
            };

            var status = _dataSource.Database_SelfCheck();
            Assert.AreEqual(EnumDatabaseStatus.OK, status.Status, status.ExtendInfo);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _dataSource?.Database_CloseConnection();
            System.Data.SQLite.SQLiteConnection.ClearAllPools();

            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }

        [TestMethod]
        public void SqliteDataSource_CanCreateReadUpdateDeleteAndProtectSecrets()
        {
            var rdp = new RDP
            {
                DisplayName = "RDP test",
                Address = "192.0.2.10",
                UserName = "rdp-user",
                Password = "rdp-password"
            };
            var ssh = new SSH
            {
                DisplayName = "SSH test",
                Address = "192.0.2.20",
                UserName = "ssh-user",
                PrivateKey = "private-key"
            };
            var vnc = new VNC
            {
                DisplayName = "VNC test",
                Address = "192.0.2.30",
                Password = "vnc-password"
            };
            var localApp = new LocalApp
            {
                DisplayName = "App test",
                ExePath = "example.exe"
            };

            ProtocolBase[] servers = [rdp, ssh, vnc, localApp];
            foreach (var server in servers)
            {
                var insert = _dataSource.Database_InsertServer(server);
                Assert.IsTrue(insert.IsSuccess, insert.ErrorInfo);
                Assert.IsFalse(string.IsNullOrWhiteSpace(server.Id));
            }

            var stored = _dataSource.GetDataBase().GetServers();
            Assert.IsTrue(stored.IsSuccess, stored.ErrorInfo);
            Assert.HasCount(4, stored.Items);

            var storedRdp = stored.Items.OfType<RDP>().Single();
            Assert.AreNotEqual(rdp.Password, storedRdp.Password);
            StringAssert.StartsWith(storedRdp.Password, "rmsec:1:");
            storedRdp.DecryptToConnectLevel();
            Assert.AreEqual(rdp.Password, storedRdp.Password);

            var storedSsh = stored.Items.OfType<SSH>().Single();
            Assert.AreNotEqual(ssh.PrivateKey, storedSsh.PrivateKey);

            storedSsh.DecryptToConnectLevel();
            Assert.AreEqual(ssh.PrivateKey, storedSsh.PrivateKey);

            rdp.DisplayName = "RDP updated";
            rdp.Address = "198.51.100.10";
            var update = _dataSource.Database_UpdateServer(rdp);
            Assert.IsTrue(update.IsSuccess, update.ErrorInfo);

            var updated = _dataSource.GetDataBase().GetServers();
            Assert.IsTrue(updated.IsSuccess, updated.ErrorInfo);
            var updatedRdp = updated.Items.OfType<RDP>().Single();
            Assert.AreEqual(rdp.DisplayName, updatedRdp.DisplayName);
            Assert.AreEqual(rdp.Address, updatedRdp.Address);

            var delete = _dataSource.Database_DeleteServer([rdp.Id, ssh.Id]);
            Assert.IsTrue(delete.IsSuccess, delete.ErrorInfo);

            var remaining = _dataSource.GetDataBase().GetServers();
            Assert.IsTrue(remaining.IsSuccess, remaining.ErrorInfo);
            Assert.HasCount(2, remaining.Items);
            Assert.IsFalse(remaining.Items.Any(x => x.Id == rdp.Id || x.Id == ssh.Id));
        }
    }
}
