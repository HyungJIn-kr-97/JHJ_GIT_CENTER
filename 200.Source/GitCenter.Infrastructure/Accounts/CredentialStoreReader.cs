using System.Runtime.InteropServices;

namespace GitCenter.Infrastructure.Accounts;

/// <summary>Windows 자격 증명 관리자의 항목 이름·사용자 이름만 읽는다 — 비밀(blob)은 손대지 않는다.</summary>
/// 계약: 돌려주는 것은 (TargetName, UserName) 뿐이다 — JHJ 규약 §4 「식별 정보만 읽고 토큰은 옮겨 적지 않는다」
/// 함정: CredEnumerate 의 거름 글은 끝의 * 만 와일드카드다 — 중간 * 은 통하지 않아 호출부가 다시 거른다
public static class CredentialStoreReader
{
    public sealed record Entry(string Target, string User);

    public static IReadOnlyList<Entry> Enumerate(string filter)
    {
        var list = new List<Entry>();
        if (!CredEnumerateW(filter, 0, out var count, out var array))
        {
            return list;
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                var ptr = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIALW>(ptr);
                var target = Marshal.PtrToStringUni(cred.TargetName) ?? string.Empty;
                var user = Marshal.PtrToStringUni(cred.UserName) ?? string.Empty;
                list.Add(new Entry(target, user));
            }
        }
        finally
        {
            CredFree(array);
        }

        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerateW(string? filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
