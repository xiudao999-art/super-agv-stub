using System.Buffers.Binary;

namespace Kunling.RobotClient.Devices.HikvisionMobileRobot;

/// <summary>
/// 海康 GBP V2 报文头编解码器。头部固定32字节：GBP$、总长度、信令、序号、版本、加密类型、内容类型、17字节保留区。
/// 消息总长度按4字节对齐；当前实现保留加密字段但不擅自实现厂商密钥协商。
/// </summary>
public static class HikvisionFrameCodec
{
    public const int HeaderSize = 32;
    private static ReadOnlySpan<byte> Magic => "GBP$"u8;

    public static byte[] Encode(ushort signal, uint sequence, byte version,
        byte encryption, HikvisionContentType contentType, ReadOnlySpan<byte> body)
    {
        var frameLength = Align4(HeaderSize + body.Length);
        var frame = new byte[frameLength];
        Magic.CopyTo(frame);
        // 海康示例结构体运行于小端平台；所有多字节整数按小端编码。
        if (frameLength > ushort.MaxValue) throw new InvalidDataException($"海康报文超过UInt16长度上限：{frameLength}。");
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), (ushort)frameLength);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), signal);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), sequence);
        frame[12] = version;
        frame[13] = encryption;
        frame[14] = (byte)contentType;
        // 15~31 是协议V2新增的17字节保留区，数组初始化时已经清零。
        body.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static HikvisionMessage Decode(ReadOnlySpan<byte> frame, int maxFrameLength)
    {
        if (frame.Length < HeaderSize) throw new InvalidDataException("海康报文不足32字节。");
        if (!frame[..4].SequenceEqual(Magic)) throw new InvalidDataException("海康报文标识不是 GBP$。");
        var declared = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2));
        if (declared < HeaderSize || declared > maxFrameLength || declared != frame.Length || declared % 4 != 0)
            throw new InvalidDataException($"海康报文长度无效：header={declared}, received={frame.Length}。");
        var signal = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2));
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(8, 4));
        var contentType = frame[14] switch
        {
            0 => HikvisionContentType.Binary,
            1 => HikvisionContentType.Json,
            _ => throw new InvalidDataException($"不支持的协议内容类型：{frame[14]}。")
        };
        var body = frame[HeaderSize..declared].ToArray();
        // JSON末尾的零仅为结构体4字节对齐填充，不属于JSON内容。
        if (contentType == HikvisionContentType.Json)
            body = body.AsSpan(0, TrimTrailingZeros(body)).ToArray();
        return new(signal, sequence, frame[12], frame[13], contentType, body);
    }

    public static int ReadDeclaredLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || !header[..4].SequenceEqual(Magic))
            throw new InvalidDataException("无效的海康GBP消息头。");
        return BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
    }

    private static int Align4(int value) => checked((value + 3) & ~3);
    private static int TrimTrailingZeros(ReadOnlySpan<byte> body)
    {
        var length = body.Length;
        while (length > 0 && body[length - 1] == 0) length--;
        return length;
    }
}
