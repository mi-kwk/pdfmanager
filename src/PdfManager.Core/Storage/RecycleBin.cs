using System.Runtime.InteropServices;

namespace PdfManager.Core.Storage;

// 決定ログ G-1: 取り込み元のファイルは完全には消さず、ごみ箱へ移動する
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    // USB メモリなどにはごみ箱がなく、Windows は完全に削除する。呼び出し側で事前に確認すること
    public static bool IsAvailableFor(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return root is not null && !root.StartsWith(@"\\", StringComparison.Ordinal)
            && new DriveInfo(root).DriveType == DriveType.Fixed;
    }

    public static void MoveFile(string path)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // 複数指定のための形式で、ファイル名の後ろに NUL を 2 つ付ける
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
        {
            throw new IOException($"ごみ箱へ移動できませんでした(コード {result}): {path}");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
