// Unity's audio importer only reliably reads PCM WAV. A few kRO sounds are MS ADPCM (format 2) or
// IMA ADPCM (format 17); they are decoded to 16-bit PCM, which is exactly what the game plays.
static class Wav
{
    private static readonly int[] MsAdaptation = { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };
    private static readonly int[] ImaIndex = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
    private static readonly int[] ImaStep =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
        1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    };

    public static int FormatTag(byte[] wav) => Chunk(wav, "fmt ") is { } f && f.Length >= 2 ? BitConverter.ToUInt16(wav, f.Offset) : -1;

    // Returns PCM for ADPCM input, the input itself otherwise, and a note describing any change.
    public static (byte[] Bytes, string? Note) ForUnity(byte[] wav)
    {
        var tag = FormatTag(wav);
        if (tag is not (2 or 17))
            return (wav, null);
        try
        {
            var pcm = tag == 2 ? DecodeMs(wav) : DecodeIma(wav);
            return (pcm, $"decoded from {(tag == 2 ? "MS" : "IMA")} ADPCM to PCM (Unity cannot import ADPCM WAV)");
        }
        catch (Exception ex)
        {
            return (wav, $"ADPCM decode failed: {ex.Message}");
        }
    }

    private record struct ChunkRef(int Offset, int Length);

    private static ChunkRef? Chunk(byte[] wav, string id)
    {
        if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
            return null;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var cid = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var len = BitConverter.ToInt32(wav, pos + 4);
            if (len < 0)
                return null;
            if (cid == id)
                return new ChunkRef(pos + 8, Math.Min(len, wav.Length - pos - 8));
            pos += 8 + len + (len & 1);
        }

        return null;
    }

    private static (int Channels, int Rate, int BlockAlign, int SamplesPerBlock, ChunkRef Data, ChunkRef Fmt) Header(byte[] wav)
    {
        var fmt = Chunk(wav, "fmt ") ?? throw new InvalidDataException("no fmt chunk");
        var data = Chunk(wav, "data") ?? throw new InvalidDataException("no data chunk");
        int channels = BitConverter.ToUInt16(wav, fmt.Offset + 2);
        var rate = BitConverter.ToInt32(wav, fmt.Offset + 4);
        int blockAlign = BitConverter.ToUInt16(wav, fmt.Offset + 12);
        var samplesPerBlock = fmt.Length >= 22 ? BitConverter.ToUInt16(wav, fmt.Offset + 18) : 0;
        if (channels is < 1 or > 2 || blockAlign <= 0)
            throw new InvalidDataException("unsupported channel layout");
        return (channels, rate, blockAlign, samplesPerBlock, data, fmt);
    }

    private static byte[] DecodeMs(byte[] wav)
    {
        var (ch, rate, align, spb, data, fmt) = Header(wav);
        var coefCount = BitConverter.ToUInt16(wav, fmt.Offset + 20);
        var coef1 = new int[coefCount];
        var coef2 = new int[coefCount];
        for (var i = 0; i < coefCount; i++)
        {
            coef1[i] = BitConverter.ToInt16(wav, fmt.Offset + 22 + i * 4);
            coef2[i] = BitConverter.ToInt16(wav, fmt.Offset + 24 + i * 4);
        }

        var output = new List<short>();
        for (var block = data.Offset; block + 7 * ch <= data.Offset + data.Length; block += align)
        {
            var end = Math.Min(block + align, data.Offset + data.Length);
            var pred = new int[ch];
            var delta = new int[ch];
            var s1 = new int[ch];
            var s2 = new int[ch];
            var p = block;
            for (var c = 0; c < ch; c++) pred[c] = Math.Min((int)wav[p++], coefCount - 1);
            for (var c = 0; c < ch; c++) { delta[c] = BitConverter.ToInt16(wav, p); p += 2; }
            for (var c = 0; c < ch; c++) { s1[c] = BitConverter.ToInt16(wav, p); p += 2; }
            for (var c = 0; c < ch; c++) { s2[c] = BitConverter.ToInt16(wav, p); p += 2; }
            for (var c = 0; c < ch; c++) output.Add((short)s2[c]);
            for (var c = 0; c < ch; c++) output.Add((short)s1[c]);

            var decoded = 2;
            var c2 = 0;
            for (; p < end && (spb == 0 || decoded < spb); p++)
            {
                foreach (var nibble in new[] { wav[p] >> 4, wav[p] & 0x0f })
                {
                    var signed = nibble >= 8 ? nibble - 16 : nibble;
                    var predicted = ((s1[c2] * coef1[pred[c2]]) + (s2[c2] * coef2[pred[c2]])) >> 8;
                    predicted = Math.Clamp(predicted + signed * delta[c2], short.MinValue, short.MaxValue);
                    s2[c2] = s1[c2];
                    s1[c2] = predicted;
                    delta[c2] = Math.Max(16, (MsAdaptation[nibble] * delta[c2]) >> 8);
                    output.Add((short)predicted);
                    c2 = (c2 + 1) % ch;
                    if (c2 == 0)
                        decoded++;
                }
            }
        }

        return Pcm16(output, ch, rate);
    }

    private static byte[] DecodeIma(byte[] wav)
    {
        var (ch, rate, align, _, data, _) = Header(wav);
        var output = new List<short>();
        for (var block = data.Offset; block + 4 * ch <= data.Offset + data.Length; block += align)
        {
            var end = Math.Min(block + align, data.Offset + data.Length);
            var sample = new int[ch];
            var index = new int[ch];
            var p = block;
            for (var c = 0; c < ch; c++)
            {
                sample[c] = BitConverter.ToInt16(wav, p);
                index[c] = Math.Clamp((int)wav[p + 2], 0, 88);
                p += 4;
            }

            var perChannel = new List<short>[ch];
            for (var c = 0; c < ch; c++)
                perChannel[c] = new List<short> { (short)sample[c] };

            // Mono: consecutive bytes, low nibble first. Stereo: 4 bytes of one channel, then the other.
            while (p < end)
            {
                for (var c = 0; c < ch && p < end; c++)
                {
                    var bytes = ch == 1 ? 1 : 4;
                    for (var b = 0; b < bytes && p < end; b++, p++)
                        foreach (var nibble in new[] { wav[p] & 0x0f, wav[p] >> 4 })
                        {
                            var step = ImaStep[index[c]];
                            var diff = step >> 3;
                            if ((nibble & 1) != 0) diff += step >> 2;
                            if ((nibble & 2) != 0) diff += step >> 1;
                            if ((nibble & 4) != 0) diff += step;
                            if ((nibble & 8) != 0) diff = -diff;
                            sample[c] = Math.Clamp(sample[c] + diff, short.MinValue, short.MaxValue);
                            index[c] = Math.Clamp(index[c] + ImaIndex[nibble], 0, 88);
                            perChannel[c].Add((short)sample[c]);
                        }
                }
            }

            var frames = perChannel.Min(l => l.Count);
            for (var i = 0; i < frames; i++)
                for (var c = 0; c < ch; c++)
                    output.Add(perChannel[c][i]);
        }

        return Pcm16(output, ch, rate);
    }

    private static byte[] Pcm16(List<short> samples, int channels, int rate)
    {
        using var ms = new MemoryStream(44 + samples.Count * 2);
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + samples.Count * 2);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(rate);
        bw.Write(rate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write("data"u8.ToArray());
        bw.Write(samples.Count * 2);
        foreach (var s in samples)
            bw.Write(s);
        return ms.ToArray();
    }
}
