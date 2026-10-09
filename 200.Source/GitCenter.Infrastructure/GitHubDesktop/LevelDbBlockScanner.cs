namespace GitCenter.Infrastructure.GitHubDesktop;

/// <summary>
/// LevelDB 의 .ldb(SSTable)·.log 파일에서 블록 알맹이만 꺼내 준다 — 키·값 구조까지는 풀지 않는다.
/// 왜: 우리에게 필요한 것은 「경로 문자열이 어딘가 들어 있다」뿐이라, IndexedDB 레코드 형식(V8 직렬화)까지 풀 이유가 없다.
/// 계약: 못 읽는 파일은 조용히 건너뛴다 — GitHub Desktop 이 돌고 있어도 읽기 전용으로 연다.
/// </summary>
internal static class LevelDbBlockScanner
{
    private const ulong TableMagic = 0xdb4775248b80fb57UL;
    private const int FooterSize = 48;

    /// <summary>파일 하나에서 블록들의 알맹이(압축 푼 바이트)를 차례로 돌려준다.</summary>
    public static IEnumerable<byte[]> ReadBlocks(string path)
    {
        byte[] file;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            file = memory.ToArray();
        }
        catch
        {
            yield break;
        }

        if (path.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".sst", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var block in ReadTableBlocks(file))
            {
                yield return block;
            }

            yield break;
        }

        // 계약: .log(WAL)은 눌려 있지 않다 — 통째로 한 덩어리로 넘긴다
        yield return file;
    }

    private static IEnumerable<byte[]> ReadTableBlocks(byte[] file)
    {
        if (file.Length < FooterSize)
        {
            yield break;
        }

        var footerAt = file.Length - FooterSize;
        var magic = BitConverter.ToUInt64(file, file.Length - 8);
        if (magic != TableMagic)
        {
            yield break;
        }

        var at = footerAt;
        if (!TryReadHandle(file, ref at, out _, out _))
        {
            yield break;
        }

        if (!TryReadHandle(file, ref at, out var indexOffset, out var indexSize))
        {
            yield break;
        }

        var indexBlock = ReadBlockContents(file, indexOffset, indexSize);
        if (indexBlock is null)
        {
            yield break;
        }

        foreach (var (_, value) in ReadBlockEntries(indexBlock))
        {
            var cursor = 0;
            if (!TryReadHandle(value, ref cursor, out var dataOffset, out var dataSize))
            {
                continue;
            }

            var data = ReadBlockContents(file, dataOffset, dataSize);
            if (data is not null)
            {
                yield return data;
            }
        }
    }

    /// 계약: 블록 뒤에는 압축 종류 1바이트 + CRC 4바이트가 붙는다. 0=없음 · 1=snappy, 그 밖(zstd 등)은 건너뛴다
    private static byte[]? ReadBlockContents(byte[] file, long offset, long size)
    {
        if (offset < 0 || size <= 0 || offset + size + 5 > file.Length)
        {
            return null;
        }

        var kind = file[(int)(offset + size)];
        var span = file.AsSpan((int)offset, (int)size);

        if (kind == 0)
        {
            return span.ToArray();
        }

        if (kind == 1 && Snappy.TryDecode(span, out var decoded))
        {
            return decoded;
        }

        return null;
    }

    /// <summary>블록 안의 (키, 값) 쌍 — 키는 앞 글자를 공유하는 압축 형식이다.</summary>
    private static IEnumerable<(byte[] Key, byte[] Value)> ReadBlockEntries(byte[] block)
    {
        if (block.Length < 4)
        {
            yield break;
        }

        var restartCount = BitConverter.ToInt32(block, block.Length - 4);
        if (restartCount < 0 || restartCount > block.Length / 4)
        {
            yield break;
        }

        var limit = block.Length - 4 - restartCount * 4;
        if (limit <= 0)
        {
            yield break;
        }

        var at = 0;
        var previousKey = Array.Empty<byte>();

        while (at < limit)
        {
            if (!TryReadVarint(block, ref at, out var shared) ||
                !TryReadVarint(block, ref at, out var nonShared) ||
                !TryReadVarint(block, ref at, out var valueLength))
            {
                yield break;
            }

            if (shared > previousKey.Length || at + nonShared + valueLength > block.Length)
            {
                yield break;
            }

            var key = new byte[shared + nonShared];
            Array.Copy(previousKey, key, shared);
            Array.Copy(block, at, key, shared, nonShared);
            at += (int)nonShared;

            var value = new byte[valueLength];
            Array.Copy(block, at, value, 0, valueLength);
            at += (int)valueLength;

            previousKey = key;
            yield return (key, value);
        }
    }

    private static bool TryReadHandle(byte[] data, ref int at, out long offset, out long size)
    {
        offset = 0;
        size = 0;
        if (!TryReadVarint(data, ref at, out var o) || !TryReadVarint(data, ref at, out var s))
        {
            return false;
        }

        offset = o;
        size = s;
        return true;
    }

    private static bool TryReadVarint(byte[] data, ref int at, out long value)
    {
        value = 0;
        var shift = 0;
        while (at < data.Length && shift <= 63)
        {
            var b = data[at++];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }
}
