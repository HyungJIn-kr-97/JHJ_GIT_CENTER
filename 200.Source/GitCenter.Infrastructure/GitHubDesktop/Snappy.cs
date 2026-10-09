namespace GitCenter.Infrastructure.GitHubDesktop;

/// <summary>
/// snappy raw 블록 해독기 — LevelDB 가 데이터 블록을 이것으로 눌러 둔다.
/// 왜: GitHub Desktop 의 저장소 목록은 IndexedDB(LevelDB) 안에 있고, 그 블록이 눌려 있어 그냥 읽으면 경로 문자열이 하나도 안 보인다.
/// 계약: 압축만 푼다 — 스트림 형식(0xff 0x06 "sNaPpY" 머리)은 다루지 않는다.
/// </summary>
internal static class Snappy
{
    public static bool TryDecode(ReadOnlySpan<byte> input, out byte[] output)
    {
        output = [];
        try
        {
            var at = 0;
            var length = ReadVarint(input, ref at);
            if (length <= 0 || length > 64 * 1024 * 1024)
            {
                return false;
            }

            var buffer = new byte[length];
            var write = 0;

            while (at < input.Length)
            {
                var tag = input[at++];
                var kind = tag & 0x03;

                if (kind == 0)
                {
                    var len = tag >> 2;
                    if (len >= 60)
                    {
                        var extra = len - 59;
                        if (at + extra > input.Length)
                        {
                            return false;
                        }

                        var value = 0;
                        for (var i = 0; i < extra; i++)
                        {
                            value |= input[at + i] << (8 * i);
                        }

                        at += extra;
                        len = value;
                    }

                    len += 1;
                    if (at + len > input.Length || write + len > buffer.Length)
                    {
                        return false;
                    }

                    input.Slice(at, len).CopyTo(buffer.AsSpan(write));
                    at += len;
                    write += len;
                    continue;
                }

                int copyLength, offset;
                if (kind == 1)
                {
                    if (at >= input.Length)
                    {
                        return false;
                    }

                    copyLength = 4 + ((tag >> 2) & 0x07);
                    offset = ((tag >> 5) & 0x07) << 8 | input[at++];
                }
                else if (kind == 2)
                {
                    if (at + 2 > input.Length)
                    {
                        return false;
                    }

                    copyLength = (tag >> 2) + 1;
                    offset = input[at] | input[at + 1] << 8;
                    at += 2;
                }
                else
                {
                    if (at + 4 > input.Length)
                    {
                        return false;
                    }

                    copyLength = (tag >> 2) + 1;
                    offset = input[at] | input[at + 1] << 8 | input[at + 2] << 16 | input[at + 3] << 24;
                    at += 4;
                }

                if (offset <= 0 || offset > write || write + copyLength > buffer.Length)
                {
                    return false;
                }

                // 함정: 겹치는 복사가 정상이다 — Array.Copy 로 한 번에 옮기면 반복 패턴이 깨진다
                for (var i = 0; i < copyLength; i++)
                {
                    buffer[write] = buffer[write - offset];
                    write++;
                }
            }

            output = write == buffer.Length ? buffer : buffer.AsSpan(0, write).ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadVarint(ReadOnlySpan<byte> data, ref int at)
    {
        var result = 0;
        var shift = 0;
        while (at < data.Length && shift <= 28)
        {
            var b = data[at++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        return -1;
    }
}
