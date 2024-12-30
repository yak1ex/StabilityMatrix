using System.Buffers.Binary;
using System.Text;
using StabilityMatrix.Core.Models;

namespace StabilityMatrix.Tests.Models;

[TestClass]
public class SafetensorMetadataTests
{
    [TestMethod]
    public void TestParseStream()
    {
        const string SOURCE_JSON = """
{
"anything":[1,2,3,4,"",{ "a": 1, "b": 2, "c": 3 }],
"__metadata__":{"ss_network_module":"some network module","modelspec.architecture":"some architecture",
    "ss_tag_frequency":"{\"aaa\":{\"tag1\":59,\"tag2\":2},\"bbb\":{\"tag1\":4,\"tag3\":1}}" },
"someotherdata":{ "a": 1, "b": 2, "c": 3 }
}
""";

        var stream = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)SOURCE_JSON.Length);
        stream.Write(buffer);
        stream.Write(Encoding.UTF8.GetBytes(SOURCE_JSON));
        stream.Position = 0;

        Assert.IsTrue(SafetensorMetadata.TryParse(stream, out var metadata));

        // Assert.AreEqual("some network module", metadata.NetworkModule);
        // Assert.AreEqual("some architecture", metadata.ModelSpecArchitecture);

        Assert.IsNotNull(metadata.TagFrequency);
        CollectionAssert.AreEqual(
            new List<SafetensorMetadata.Tag> { ("tag1", 63), ("tag2", 2), ("tag3", 1) },
            metadata.TagFrequency
        );
    }
}
