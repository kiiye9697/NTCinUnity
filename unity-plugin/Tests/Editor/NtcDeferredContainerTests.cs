using System;
using System.IO;
using System.Text;
using Ntc.Packing;
using NUnit.Framework;

namespace Ntc.Tests
{
    public class NtcDeferredContainerTests
    {
        static string Container()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ntc");
            byte[] json = Encoding.UTF8.GetBytes("{\"views\":[{\"offset\":3,\"storedSize\":8}]}");
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(Encoding.ASCII.GetBytes("NTEX")); writer.Write((uint)1);
                writer.Write((ulong)40); writer.Write((ulong)json.Length);
                writer.Write((ulong)(40 + json.Length)); writer.Write((ulong)16);
                writer.Write(json);
                for (byte i = 0; i < 16; i++) writer.Write(i);
            }
            return path;
        }

        [Test]
        public void DeferredViewMatchesEagerWithoutRetainingChunk()
        {
            string path = Container();
            try
            {
                var eager = NtcContainerReader.Load(path);
                var deferred = NtcContainerReader.LoadMetadata(path);
                Assert.That(deferred.DataChunk, Is.Null);
                CollectionAssert.AreEqual(eager.GetViewBytes(0), deferred.GetViewBytes(0));
                CollectionAssert.AreEqual(new byte[] {3,4,5,6,7,8,9,10}, deferred.GetViewBytes(0));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void DeferredViewRejectsTruncatedSource()
        {
            string path = Container();
            try
            {
                var deferred = NtcContainerReader.LoadMetadata(path);
                using (var stream = File.OpenWrite(path)) stream.SetLength(40);
                Assert.Throws<EndOfStreamException>(() => deferred.GetViewBytes(0));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void ViewRangeRejectsIntegerOverflow()
        {
            var container = new NtcContainer { DataChunk = new byte[16], Document = new NtcTextureSetDocument() };
            container.Document.Views.Add(new NtcBufferView {Offset = long.MaxValue, StoredSize = 8});
            Assert.Throws<InvalidDataException>(() => container.GetViewBytes(0));
        }

        [Test]
        public void MetadataRejectsChunkOutsideFile()
        {
            string path = Container();
            try
            {
                using (var writer = new BinaryWriter(File.OpenWrite(path)))
                { writer.BaseStream.Position = 32; writer.Write(ulong.MaxValue); }
                Assert.Throws<InvalidDataException>(() => NtcContainerReader.LoadMetadata(path));
            }
            finally { File.Delete(path); }
        }
    }
}
