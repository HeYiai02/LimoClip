using System.IO;
using System.Text;

namespace LiveMonitor.Plugins.Huya;

/// <summary>
/// 虎牙 Tars（Wup）二进制协议最小子集编码器，仅覆盖弹幕/订阅/握手用到的类型。
/// <para>
/// 编码规则（与虎牙线上协议一致）：头字节 = (tag &lt;&lt; 4) | type，tag 大于等于 15 时头字节低 4 位为 15、后跟 1 字节真实 tag；
/// 整型大端；字符串为 UTF-8（type 6 长度 1 字节，type 7 长度 4 字节）；bytes 用 type 13（SimpleList）。
/// </para>
/// </summary>
internal sealed class HuyaTarsWriter
{
    private const int TypeByte = 0;
    private const int TypeShort = 1;
    private const int TypeInt = 2;
    private const int TypeLong = 3;
    private const int TypeString1 = 6;
    private const int TypeString4 = 7;
    private const int TypeList = 9;
    private const int TypeZeroTag = 12;
    private const int TypeSimpleList = 13;

    private readonly MemoryStream _buffer = new();

    public void Write(long value, int tag)
    {
        if (value == 0)
        {
            WriteHead(TypeZeroTag, tag);
        }
        else if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            WriteHead(TypeByte, tag);
            _buffer.WriteByte((byte)value);
        }
        else if (value is >= short.MinValue and <= short.MaxValue)
        {
            WriteHead(TypeShort, tag);
            WriteBigEndian(value, 2);
        }
        else if (value is >= int.MinValue and <= int.MaxValue)
        {
            WriteHead(TypeInt, tag);
            WriteBigEndian(value, 4);
        }
        else
        {
            WriteHead(TypeLong, tag);
            WriteBigEndian(value, 8);
        }
    }

    public void Write(bool value, int tag) => Write(value ? 1 : 0, tag);

    public void Write(string value, int tag)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > 255)
        {
            WriteHead(TypeString4, tag);
            WriteBigEndian(bytes.Length, 4);
        }
        else
        {
            WriteHead(TypeString1, tag);
            _buffer.WriteByte((byte)bytes.Length);
        }

        _buffer.Write(bytes, 0, bytes.Length);
    }

    public void Write(byte[] value, int tag)
    {
        WriteHead(TypeSimpleList, tag);
        WriteHead(TypeByte, 0);
        Write(value.Length, 0);
        _buffer.Write(value, 0, value.Length);
    }

    /// <summary>写 <c>list&lt;string&gt;</c>：LIST 头 + tag0 长度 + 逐个 tag0 字符串。</summary>
    public void WriteStringList(IReadOnlyList<string> values, int tag)
    {
        WriteHead(TypeList, tag);
        Write(values.Count, 0);
        foreach (var value in values)
        {
            Write(value, 0);
        }
    }

    /// <summary>写结构体开始头（tag0 缩进即可）；与 <see cref="EndStruct"/> 成对使用。</summary>
    public void BeginStruct(int tag = 0) => WriteHead(10, tag);

    /// <summary>写结构体结束头。</summary>
    public void EndStruct() => WriteHead(11, 0);

    /// <summary>写 LIST 开始头；调用方随后写 tag0 元素个数与各元素。</summary>
    public void BeginList(int tag = 0) => WriteHead(TypeList, tag);

    public byte[] ToArray() => _buffer.ToArray();

    private void WriteHead(int type, int tag)
    {
        if (tag < 15)
        {
            _buffer.WriteByte((byte)((tag << 4) | type));
        }
        else if (tag < 256)
        {
            _buffer.WriteByte((byte)((15 << 4) | type));
            _buffer.WriteByte((byte)tag);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(tag), tag, "Tars tag 超出支持范围。");
        }
    }

    private void WriteBigEndian(long value, int length)
    {
        for (var i = length - 1; i >= 0; i--)
        {
            _buffer.WriteByte((byte)((value >> (i * 8)) & 0xFF));
        }
    }
}

/// <summary>
/// 虎牙 Tars 协议最小子集只读解码器。头字节：type = b &amp; 0x0F，tag = (b &gt;&gt; 4) &amp; 0x0F；tag 为 15 时后跟真实 tag。
/// 字段缺失时返回类型默认值（0 / 空串 / 空数组），便于在服务端字段变化时容错。
/// </summary>
internal sealed class HuyaTarsReader
{
    private const int TypeByte = 0;
    private const int TypeShort = 1;
    private const int TypeInt = 2;
    private const int TypeLong = 3;
    private const int TypeFloat = 4;
    private const int TypeDouble = 5;
    private const int TypeString1 = 6;
    private const int TypeString4 = 7;
    private const int TypeMap = 8;
    private const int TypeList = 9;
    private const int TypeStructBegin = 10;
    private const int TypeStructEnd = 11;
    private const int TypeZeroTag = 12;
    private const int TypeSimpleList = 13;

    private readonly byte[] _data;
    private int _position;

    public HuyaTarsReader(byte[] data, int position = 0)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _position = position;
    }

    public long ReadInt(int tag)
    {
        if (!SkipToTag(tag))
        {
            return 0;
        }

        var (type, _) = ReadHead();
        return type switch
        {
            TypeZeroTag => 0,
            TypeByte => _data[_position++],
            TypeShort => ReadBigEndian(2),
            TypeInt => ReadBigEndian(4),
            TypeLong => ReadBigEndian(8),
            _ => throw new InvalidDataException($"Tars 整型字段类型不匹配：type={type}")
        };
    }

    public string ReadString(int tag)
    {
        if (!SkipToTag(tag))
        {
            return string.Empty;
        }

        var (type, _) = ReadHead();
        var length = type switch
        {
            TypeString1 => _data[_position++],
            TypeString4 => checked((int)ReadBigEndian(4)),
            _ => throw new InvalidDataException($"Tars 字符串字段类型不匹配：type={type}")
        };

        EnsureAvailable(length);
        var value = Encoding.UTF8.GetString(_data, _position, length);
        _position += length;
        return value;
    }

    public byte[] ReadBytes(int tag)
    {
        if (!SkipToTag(tag))
        {
            return Array.Empty<byte>();
        }

        var (type, _) = ReadHead();
        if (type != TypeSimpleList)
        {
            throw new InvalidDataException($"Tars bytes 字段类型不匹配：type={type}");
        }

        // SimpleList 内层是 tag0 的长度字段。
        ReadHead();
        var length = checked((int)ReadLengthTag0());
        EnsureAvailable(length);
        var bytes = new byte[length];
        Array.Copy(_data, _position, bytes, 0, length);
        _position += length;
        return bytes;
    }

    /// <summary>进入 tag 处的结构体（消费 STRUCT_BEGIN 头）；字段读完调用 <see cref="EndStruct"/>。</summary>
    public bool EnterStruct(int tag)
    {
        if (!SkipToTag(tag))
        {
            return false;
        }

        var (type, _) = ReadHead();
        return type == TypeStructBegin;
    }

    /// <summary>进入 tag 处的 LIST，返回元素数量。</summary>
    public int EnterList(int tag)
    {
        if (!SkipToTag(tag))
        {
            return 0;
        }

        var (type, _) = ReadHead();
        return type == TypeList ? (int)ReadLengthTag0() : 0;
    }

    /// <summary>消费列表中一个结构体元素的 STRUCT_BEGIN 头。</summary>
    public bool EnterStructElement()
    {
        if (_position >= _data.Length)
        {
            return false;
        }

        var (type, _) = ReadHead();
        return type == TypeStructBegin;
    }

    /// <summary>同步到结构体结束；字段可变时尽力而为，失败不影响已读出的字段。</summary>
    public void EndStruct()
    {
        try
        {
            SkipToStructEnd();
        }
        catch (Exception)
        {
            // 服务端字段变化或截断时容错，不向上抛。
        }
    }

    private (int Type, int Tag) ReadHead()
    {
        EnsureAvailable(1);
        var head = _data[_position++];
        var type = head & 0x0F;
        var tag = (head >> 4) & 0x0F;
        if (tag == 15)
        {
            EnsureAvailable(1);
            tag = _data[_position++];
        }

        return (type, tag);
    }

    private bool SkipToTag(int tag)
    {
        while (_position < _data.Length)
        {
            var start = _position;
            var (type, current) = ReadHead();
            if (tag <= current || type == TypeStructEnd)
            {
                _position = start;
                return tag == current && type != TypeStructEnd;
            }

            SkipField(type);
        }

        return false;
    }

    private void SkipToStructEnd()
    {
        while (_position < _data.Length)
        {
            var (type, _) = ReadHead();
            SkipField(type);
            if (type == TypeStructEnd)
            {
                return;
            }
        }
    }

    private void SkipField(int type)
    {
        switch (type)
        {
            case TypeByte:
                EnsureAvailable(1);
                _position += 1;
                break;
            case TypeZeroTag:
            case TypeStructEnd:
                break;
            case TypeShort:
                EnsureAvailable(2);
                _position += 2;
                break;
            case TypeInt:
            case TypeFloat:
                EnsureAvailable(4);
                _position += 4;
                break;
            case TypeLong:
            case TypeDouble:
                EnsureAvailable(8);
                _position += 8;
                break;
            case TypeString1:
                EnsureAvailable(1);
                _position += 1 + _data[_position];
                EnsureAvailable(0);
                break;
            case TypeString4:
            {
                var length = checked((int)ReadBigEndian(4));
                EnsureAvailable(length);
                _position += length;
                break;
            }
            case TypeMap:
            case TypeList:
            {
                var size = checked((int)ReadLengthTag0());
                var fields = type == TypeMap ? size * 2 : size;
                for (var i = 0; i < fields; i++)
                {
                    var (fieldType, _) = ReadHead();
                    SkipField(fieldType);
                }

                break;
            }
            case TypeSimpleList:
            {
                var (_, _) = ReadHead();
                var size = checked((int)ReadLengthTag0());
                EnsureAvailable(size);
                _position += size;
                break;
            }
            case TypeStructBegin:
                SkipToStructEnd();
                break;
            default:
                throw new InvalidDataException($"Tars 跳过未知类型：type={type}");
        }
    }

    /// <summary>读 tag0 的整型长度字段（Map/List/SimpleList 的 size）。</summary>
    private long ReadLengthTag0()
    {
        var (type, _) = ReadHead();
        return type switch
        {
            TypeZeroTag => 0,
            TypeByte => _data[_position++],
            TypeShort => ReadBigEndian(2),
            TypeInt => ReadBigEndian(4),
            TypeLong => ReadBigEndian(8),
            _ => throw new InvalidDataException($"Tars 长度字段类型不匹配：type={type}")
        };
    }

    private long ReadBigEndian(int length)
    {
        EnsureAvailable(length);
        long value = 0;
        for (var i = 0; i < length; i++)
        {
            value = (value << 8) | _data[_position++];
        }

        return value;
    }

    private void EnsureAvailable(int length)
    {
        if (length < 0 || _position + length > _data.Length)
        {
            throw new InvalidDataException($"Tars 数据长度越界：需要 {length} 字节，剩余 {_data.Length - _position} 字节。");
        }
    }
}
