using System.Diagnostics;

namespace PdfManager.Core.Update;

// 選んだ exe を確かめた結果(決定ログ HA-2)
public enum ExeCheckStatus
{
    Newer,
    SameOrOlder,
    NotPdfManager,
    // 開発用の exe など、1 つのファイルだけでは動かないもの
    NotStandalone,
    SameFile,
}

public sealed record ExeCheck(ExeCheckStatus Status, string? VersionText);

// アプリの更新(決定ログ HA-1〜HA-3・HA-5)。インストーラーは使わず、置いてある exe を新しいものに差し替える。
// 動いている exe は名前を変えると、そのあと exe の中の部品を読めなくなって落ちる。そのため、
// ① 新しい exe を同じフォルダに「〇〇.new.exe」として写す(Stage)→ ② 今の PdfManager を終える →
// ③ 「〇〇.new.exe」を起動し、前の PdfManager が終わってから、今の exe を「〇〇.old.exe」に変えて新しい exe を置く(Complete)
public static class ExeUpdater
{
    public const string ProductName = "PdfManager";

    // 配布用の exe(.NET ごと 1 つにまとめたもの)は 50 MB 以上ある。開発用の exe(数百 KB)を選ばれたときに見分ける
    public const long MinimumStandaloneSize = 20L << 20;

    // バージョンは exe を作った日時(年.月.日.時分)
    public static Version? ParseVersion(string? text) =>
        Version.TryParse(text?.Split('+')[0].Trim(), out var version) ? version : null;

    public static string OldPath(string exePath) => Sibling(exePath, ".old.exe");

    public static string StagingPath(string exePath) => Sibling(exePath, ".new.exe");

    private static string Sibling(string exePath, string suffix) =>
        Path.Combine(Path.GetDirectoryName(exePath)!, Path.GetFileNameWithoutExtension(exePath) + suffix);

    public static ExeCheck Check(string candidatePath, string currentExePath, Version currentVersion)
    {
        if (string.Equals(Path.GetFullPath(candidatePath), Path.GetFullPath(currentExePath), StringComparison.OrdinalIgnoreCase))
        {
            return new ExeCheck(ExeCheckStatus.SameFile, null);
        }
        FileVersionInfo info;
        try
        {
            info = FileVersionInfo.GetVersionInfo(candidatePath);
        }
        catch (FileNotFoundException)
        {
            return new ExeCheck(ExeCheckStatus.NotPdfManager, null);
        }
        return Evaluate(info.ProductName, info.ProductVersion, new FileInfo(candidatePath).Length, currentVersion);
    }

    public static ExeCheck Evaluate(string? productName, string? versionText, long size, Version currentVersion)
    {
        var version = ParseVersion(versionText);
        if (productName != ProductName || version is null)
        {
            return new ExeCheck(ExeCheckStatus.NotPdfManager, null);
        }
        // 表示は exe に書かれたとおり(時分の「0930」の 0 を残す)
        var text = versionText!.Split('+')[0].Trim();
        if (size < MinimumStandaloneSize)
        {
            return new ExeCheck(ExeCheckStatus.NotStandalone, text);
        }
        return new ExeCheck(version > currentVersion ? ExeCheckStatus.Newer : ExeCheckStatus.SameOrOlder, text);
    }

    // 置いてある場所に書き込めるか(決定ログ HA-3。Program Files などは書き込めない)
    public static bool CanWrite(string currentExePath)
    {
        var probe = Path.Combine(Path.GetDirectoryName(currentExePath)!, $".pdfmanager-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ① 新しい exe を今の exe と同じフォルダに「〇〇.new.exe」として写す。今の exe には触らない
    public static string Stage(string currentExePath, string newExePath)
    {
        var staging = StagingPath(currentExePath);
        try
        {
            File.Copy(newExePath, staging, overwrite: true);
            if (new FileInfo(staging).Length != new FileInfo(newExePath).Length)
            {
                throw new IOException("新しい exe を最後まで写せませんでした。");
            }
            return staging;
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    // ③ 前の PdfManager が終わってから呼ぶ。今の exe を「〇〇.old.exe」に変え(前の old は消す。HA-5 で 1 つだけ残す)、
    // 写しておいた新しい exe を元の名前で置く。失敗したら元に戻す
    public static void Complete(string targetExePath, string stagedExePath)
    {
        var old = OldPath(targetExePath);
        var work = targetExePath + ".tmp";
        File.Copy(stagedExePath, work, overwrite: true);
        try
        {
            if (File.Exists(old))
            {
                File.Delete(old);
            }
            File.Move(targetExePath, old);
            try
            {
                File.Move(work, targetExePath);
            }
            catch
            {
                File.Move(old, targetExePath);
                throw;
            }
        }
        finally
        {
            TryDelete(work);
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
