using LiveMonitor.Plugins.Huya;
using System.IO;
using System.Text;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Huya;

public sealed class HuyaTarsTests
{
    [Fact]
    public void Writer_ZeroValue_WritesZeroTag()
    {
        var writer = new HuyaTarsWriter();

        writer.Write(0, 0);

        Assert.Equal(new byte[] { 0x0C }, writer.ToArray());
    }

    [Fact]
    public void Writer_ByteValue_WritesHeadAndValue()
    {
        var writer = new HuyaTarsWriter();

        writer.Write(16, 0);

        Assert.Equal(new byte[] { 0x00, 0x10 }, writer.ToArray());
    }

    [Fact]
    public void Writer_IntValue_WritesBigEndian()
    {
        var writer = new HuyaTarsWriter();

        writer.Write(100000, 0);

        Assert.Equal(new byte[] { 0x02, 0x00, 0x01, 0x86, 0xA0 }, writer.ToArray());
    }

    [Fact]
    public void Writer_String_WritesLengthPrefixedUtf8()
    {
        var writer = new HuyaTarsWriter();

        writer.Write("直播", 2);

        Assert.Equal(new byte[] { 0x26, 0x06, 0xE7, 0x9B, 0xB4, 0xE6, 0x92, 0xAD }, writer.ToArray());
    }

    [Fact]
    public void Writer_Tag15_WritesExtendedTagByte()
    {
        var writer = new HuyaTarsWriter();

        writer.Write(1, 15);

        Assert.Equal(new byte[] { 0xF0, 0x0F, 0x01 }, writer.ToArray());
    }

    [Fact]
    public void Writer_StringList_WritesListHeadCountAndItems()
    {
        var writer = new HuyaTarsWriter();

        writer.WriteStringList(new[] { "a", "bc" }, 0);

        Assert.Equal(
            new byte[] { 0x09, 0x00, 0x02, 0x06, 0x01, 0x61, 0x06, 0x02, 0x62, 0x63 },
            writer.ToArray());
    }

    [Fact]
    public void Reader_RoundTripsWriterOutput()
    {
        var writer = new HuyaTarsWriter();
        writer.Write(5, 0);
        writer.Write("hello", 1);
        writer.Write(1400, 2);
        writer.Write(string.Empty, 3);

        var reader = new HuyaTarsReader(writer.ToArray());

        Assert.Equal(5, reader.ReadInt(0));
        Assert.Equal("hello", reader.ReadString(1));
        Assert.Equal(1400, reader.ReadInt(2));
        Assert.Equal(string.Empty, reader.ReadString(3));
    }

    [Fact]
    public void Reader_SkipsUnknownIntermediateFields()
    {
        var writer = new HuyaTarsWriter();
        writer.Write(5, 0);
        writer.Write("skip-me", 1);
        writer.Write(7, 2);

        var reader = new HuyaTarsReader(writer.ToArray());

        Assert.Equal(7, reader.ReadInt(2));
    }

    [Fact]
    public void Reader_TruncatedString4_ThrowsInvalidData()
    {
        // type 7（string4, tag0）声明长度 1MB，但后面没有数据。
        var data = new byte[] { 0x07, 0x00, 0x10, 0x00, 0x00 };
        var reader = new HuyaTarsReader(data);

        Assert.Throws<InvalidDataException>(() => reader.ReadString(0));
    }

    [Fact]
    public void Reader_TypeMismatch_ThrowsInvalidData()
    {
        var writer = new HuyaTarsWriter();
        writer.Write("text", 0);
        var reader = new HuyaTarsReader(writer.ToArray());

        Assert.Throws<InvalidDataException>(() => reader.ReadInt(0));
    }

    [Fact]
    public void Reader_SimpleListWithHugeLength_Throws()
    {
        // SimpleList 头 0x0D，内层头 0x00，长度字段声明 0x00FFFFFF。
        var data = new byte[] { 0x0D, 0x00, 0x02, 0x00, 0xFF, 0xFF, 0xFF };
        var reader = new HuyaTarsReader(data);

        Assert.Throws<InvalidDataException>(() => reader.ReadBytes(0));
    }

    [Fact]
    public void Reader_EnterStructReadsNestedFields()
    {
        var writer = new HuyaTarsWriter();
        writer.BeginStruct(0);
        writer.Write(123, 0);
        writer.Write("nick", 2);
        writer.EndStruct();

        var reader = new HuyaTarsReader(writer.ToArray());

        Assert.True(reader.EnterStruct(0));
        Assert.Equal(123, reader.ReadInt(0));
        Assert.Equal("nick", reader.ReadString(2));
        reader.EndStruct();
    }

    [Fact]
    public void Writer_SimpleListBytes_MatchesReader()
    {
        var payload = Encoding.UTF8.GetBytes("abc");
        var writer = new HuyaTarsWriter();
        writer.Write(payload, 1);

        var reader = new HuyaTarsReader(writer.ToArray());

        Assert.Equal(payload, reader.ReadBytes(1));
    }
}
