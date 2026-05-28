using System;
using System.IO;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Logging.Tests
{
    /// <summary>
    /// Verifies the rolling file sink flush policy: buffered writes are pushed to disk on
    /// Error/Critical (crash-context durability), when unflushed output crosses a size threshold,
    /// and on Dispose. Each case writes to a temp directory and reads the file back.
    /// </summary>
    [TestFixture]
    public class RollingFileFlushPolicyTests
    {
        private string _dir;

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "klog_" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

        private string ReadLogFile()
        {
            var file = Directory.GetFiles(_dir, "*.log")[0];
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }

        [Test]
        public void InfoBelowThreshold_Buffered_ThenError_FlushesBufferedTail()
        {
            var sink = new RollingFileSink("Test", 1024 * 1024, _dir);
            try
            {
                sink.Write(KLogLevel.Information, "info-1", null);
                sink.Write(KLogLevel.Information, "info-2", null);
                Assert.That(ReadLogFile(), Does.Not.Contain("info-1"), "info should still be buffered");

                sink.Write(KLogLevel.Error, "boom", null);
                var text = ReadLogFile();
                Assert.That(text, Does.Contain("info-1"));
                Assert.That(text, Does.Contain("info-2"));
                Assert.That(text, Does.Contain("boom"));
            }
            finally { sink.Dispose(); }
        }

        [Test]
        public void InfoExceedsThreshold_AutoFlushes()
        {
            var sink = new RollingFileSink("Test", 1024 * 1024, _dir);
            try
            {
                var line = new string('y', 200);
                for (int i = 0; i < 60; i++) sink.Write(KLogLevel.Information, line, null); // ~12KB > 8KB threshold
                Assert.That(ReadLogFile(), Does.Contain("yyy"));
            }
            finally { sink.Dispose(); }
        }

        [Test]
        public void Dispose_FlushesRemainder()
        {
            var sink = new RollingFileSink("Test", 1024 * 1024, _dir);
            sink.Write(KLogLevel.Information, "tail", null);
            sink.Dispose();
            Assert.That(ReadLogFile(), Does.Contain("tail"));
        }
    }
}
