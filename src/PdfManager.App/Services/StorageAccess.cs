using PdfManager.Core.Library;

namespace PdfManager.App.Services;

// メイン・サブライブラリの操作(決定ログ S-1〜S-15)を 1 つずつ、画面を止めずに行う。
// 外付け HDD への書き込みは時間がかかることがあるため、別のスレッドで動かす
public sealed class StorageAccess(LibraryStorage storage)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LibraryStorage Storage => storage;

    // サブの接続状態・名前・場所が変わったとき(画面のスレッドで呼ぶ)
    public event Action? Changed;

    // 決定ログ EB-2: ほかの PC で変わったサブを読み込んだとき(画面のスレッドで呼ぶ。一覧を読み直して知らせる)
    public event Action<IReadOnlyList<SubPullReport>>? Pulled;

    // 起動時など、ここを通らずに読み込んだ分を知らせる
    public void NotifyPulled()
    {
        var reports = storage.TakePullReports();
        if (reports.Count > 0)
        {
            Pulled?.Invoke(reports);
        }
    }

    public async Task<T> RunAsync<T>(Func<LibraryStorage, T> action)
    {
        var before = Signature();
        await _lock.WaitAsync();
        try
        {
            return await Task.Run(() => action(storage));
        }
        finally
        {
            _lock.Release();
            if (Signature() != before)
            {
                Changed?.Invoke();
            }
            NotifyPulled();
        }
    }

    public Task RunAsync(Action<LibraryStorage> action) => RunAsync(s =>
    {
        action(s);
        return true;
    });

    public Task RefreshAsync() => RunAsync(s => s.Refresh());

    private string Signature() => string.Join("\n", storage.Subs.Select(s => $"{s.Id}|{s.Name}|{s.Status}|{s.PendingPull is not null}|{s.Location?.LibraryFilePath}|{s.Candidates.Count}"));
}
