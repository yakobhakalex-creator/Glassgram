using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using Concentus;
using System.Buffers.Binary;
using System.Text;

namespace Glassgram.Telegram;

internal static class VoiceNotePipeline
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    private const int FrameSamples = 960;

    public static VoiceNoteFile Encode(string path, short[] pcm)
    {
        if (pcm.Length < SampleRate / 2) throw new InvalidOperationException("Голосовое сообщение слишком короткое.");
        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 32000;
        encoder.UseVBR = true;
        var preSkip = checked((ushort)encoder.Lookahead);
        var serial = unchecked((uint)Random.Shared.Next(int.MinValue, int.MaxValue));
        var sequence = 0u;
        var encodedSamples = 0L;

        using (var output = File.Create(path))
        {
            WritePage(output, serial, ref sequence, 0, 0x02, BuildOpusHead(preSkip));
            WritePage(output, serial, ref sequence, 0, 0, BuildOpusTags());
            var samplesToEncode = pcm.Length + preSkip;
            var frameCount = (samplesToEncode + FrameSamples - 1) / FrameSamples;
            var frame = new short[FrameSamples];
            var packet = new byte[1275];
            for (var index = 0; index < frameCount; index++)
            {
                Array.Clear(frame);
                var sourceOffset = index * FrameSamples;
                var count = Math.Min(FrameSamples, Math.Max(0, pcm.Length - sourceOffset));
                if (count > 0) Array.Copy(pcm, sourceOffset, frame, 0, count);
                var packetLength = encoder.Encode(frame, FrameSamples, packet, packet.Length);
                encodedSamples += FrameSamples;
                var last = index == frameCount - 1;
                var granule = last ? pcm.LongLength + preSkip : encodedSamples;
                WritePage(output, serial, ref sequence, granule, last ? (byte)0x04 : (byte)0, packet.AsSpan(0, packetLength));
            }
            output.Flush(true);
        }

        var decodedSamples = ValidateAndDecode(path);
        return new VoiceNoteFile(path, (int)Math.Ceiling(pcm.Length / (double)SampleRate), decodedSamples);
    }

    public static object BuildSendRequest(long chatId, VoiceNoteFile file, string waveform, object? topicId = null, object? replyTo = null) => new
    {
        @type = "sendMessage",
        chat_id = chatId,
        topic_id = topicId,
        reply_to = replyTo,
        options = (object?)null,
        reply_markup = (object?)null,
        input_message_content = new
        {
            @type = "inputMessageVoiceNote",
            voice_note = new
            {
                @type = "inputVoiceNote",
                voice_note = new { @type = "inputFileLocal", path = file.Path },
                duration = file.Duration,
                waveform
            },
            caption = new { @type = "formattedText", text = "", entities = Array.Empty<object>() },
            self_destruct_type = (object?)null
        }
    };

    public static int ValidateAndDecode(string path)
    {
        using var input = File.OpenRead(path);
        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        var reader = new OpusOggReadStream(decoder, input);
        var decoded = 0;
        while (reader.HasNextPacket)
        {
            var samples = reader.DecodeNextPacket();
            if (samples is not null) decoded += samples.Length;
        }
        if (decoded <= 0) throw new InvalidDataException("Ogg/Opus не содержит декодируемого аудио.");
        return decoded;
    }

    private static byte[] BuildOpusHead(ushort preSkip)
    {
        var packet = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(packet, 0);
        packet[8] = 1;
        packet[9] = Channels;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), SampleRate);
        return packet;
    }

    private static byte[] BuildOpusTags()
    {
        var vendor = Encoding.UTF8.GetBytes("Glassgram Concentus");
        var packet = new byte[8 + 4 + vendor.Length + 4];
        Encoding.ASCII.GetBytes("OpusTags").CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), vendor.Length);
        vendor.CopyTo(packet, 12);
        return packet;
    }

    private static void WritePage(Stream output, uint serial, ref uint sequence, long granule, byte flags, ReadOnlySpan<byte> packet)
    {
        var segments = Math.Max(1, (packet.Length + 254) / 255);
        var page = new byte[27 + segments + packet.Length];
        Encoding.ASCII.GetBytes("OggS").CopyTo(page, 0);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence++);
        page[26] = checked((byte)segments);
        var remaining = packet.Length;
        for (var i = 0; i < segments; i++)
        {
            page[27 + i] = (byte)Math.Min(255, remaining);
            remaining -= page[27 + i];
        }
        packet.CopyTo(page.AsSpan(27 + segments));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), OggCrc(page));
        output.Write(page);
    }

    private static uint OggCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }
}

internal sealed record VoiceNoteFile(string Path, int Duration, int DecodedSamples);
