using System.Buffers.Binary;
using Fh6.Core;

namespace Fh6.Core.Tests;

public class Fh6PacketTests
{
    private static FieldDef Field(string name) => Fh6Packet.Fields.Single(f => f.Name == name);
    private static int Index(string name) => Array.FindIndex(Fh6Packet.Fields, f => f.Name == name);

    [Fact]
    public void フィールド数は88()
    {
        Assert.Equal(88, Fh6Packet.Fields.Length);
    }

    [Fact]
    public void 最終オフセットは323で最後の1バイトはパディング()
    {
        var last = Fh6Packet.Fields[^1];
        Assert.Equal("norm_ai_brake_diff", last.Name);
        Assert.Equal(FieldType.S8, last.Type);
        Assert.Equal(323, last.Offset + 1);
        Assert.Equal(324, Fh6Packet.Size);
    }

    [Theory]
    [InlineData("is_race_on", 0, FieldType.I32)]
    [InlineData("car_ordinal", 212, FieldType.I32)]
    [InlineData("car_group", 232, FieldType.I32)]
    [InlineData("position_x", 244, FieldType.F32)]
    [InlineData("dist_traveled", 292, FieldType.F32)]
    [InlineData("race_pos", 314, FieldType.U8)]
    [InlineData("steer", 320, FieldType.S8)]
    public void 主要フィールドの位置と型(string name, int offset, FieldType type)
    {
        var f = Field(name);
        Assert.Equal(offset, f.Offset);
        Assert.Equal(type, f.Type);
    }

    [Fact]
    public void 手で組んだパケットから各型の値を読める()
    {
        var buf = new byte[Fh6Packet.Size];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(Field("is_race_on").Offset), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(Field("timestamp_ms").Offset), 4_000_000_000u);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(Field("car_ordinal").Offset), 3456);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(Field("car_group").Offset), -1);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(Field("position_x").Offset), -1234.5f);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(Field("dist_traveled").Offset), -12.25f);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(Field("lap_no").Offset), 65000);
        buf[Field("race_pos").Offset] = 3;
        buf[Field("gear").Offset] = 11;
        buf[Field("steer").Offset] = unchecked((byte)(sbyte)-127);

        var v = new double[Fh6Packet.Fields.Length];
        Fh6Packet.Parse(buf, v);

        Assert.Equal(1, v[Index("is_race_on")]);
        Assert.Equal(4_000_000_000d, v[Index("timestamp_ms")]);
        Assert.Equal(3456, v[Index("car_ordinal")]);
        Assert.Equal(-1, v[Index("car_group")]);
        Assert.Equal(-1234.5, v[Index("position_x")]);
        Assert.Equal(-12.25, v[Index("dist_traveled")]);
        Assert.Equal(65000, v[Index("lap_no")]);
        Assert.Equal(3, v[Index("race_pos")]);
        Assert.Equal(11, v[Index("gear")]);
        Assert.Equal(-127, v[Index("steer")]);
        // 書いていないフィールドは 0
        Assert.Equal(0, v[Index("speed")]);
    }
}
