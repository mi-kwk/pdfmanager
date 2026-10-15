using System.Runtime.InteropServices;

namespace PdfManager.Core.Text;

// ファイル名の自然な順(1, 2, 10)。エクスプローラーの「名前」順と同じ比べ方を使う(決定ログ Y-2)
public sealed class NaturalOrder : IComparer<string>
{
    public static NaturalOrder Instance { get; } = new();

    private NaturalOrder()
    {
    }

    public int Compare(string? x, string? y) => (x, y) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        _ => StrCmpLogicalW(x, y),
    };

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);
}
