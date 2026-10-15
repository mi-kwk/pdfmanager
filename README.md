# PdfManager

Windows 11 向けの、オフラインで使う PDF 管理ツールです。
大量の PDF(店舗の月次売上、報告書、稟議書、商品や資材の説明書・仕様書など)にタグや項目を付けて管理し、素早く検索・閲覧できます。

## 特長

- **ネットワーク通信を一切行いません。** PC 間のデータの受け渡しは、USB メモリと外付け HDD だけで行います。
- **インストール不要。** exe ファイル 1 つで動きます(配布先の PC に .NET を入れる必要はありません)。
- **数万件でも軽く動きます。** 1 万件で起動 3 秒以内、検索 0.5 秒以内を目標にしています。

## 主な機能

| 分類 | 内容 |
|---|---|
| 取り込み | PDF のドラッグ&ドロップ・一括取り込み、重複チェック、1 ページ目の文字からタイトル候補(スキャン PDF は OCR)、画像・ZIP から PDF を作って取り込み |
| タグ・項目 | タググループと順位(例:分類1 → 分類2 → 分類3)、自由タグ、年月・日付・数値・文字の補助項目、表記ゆれの吸収 |
| 検索 | キーワード(ひらがな・カタカナ・全角・半角を区別しない)、タグの組み合わせと除外、補助項目・評価・日付での絞り込み、検索条件の保存 |
| 一覧・閲覧 | サムネイル表示とリスト表示、タブ式のビューア、2 画面比較 |
| 文書の管理 | 版の管理、続き・関連(前編・後編など)、評価・メモ、閲覧回数の記録 |
| 保存場所 | メインのライブラリに加えて、別のドライブや外付け HDD をサブライブラリとして使える |

## 動作環境

- Windows 11(64 ビット)
- WebView2 ランタイム(Windows 11 には標準で入っています)

## 使い方

1. `PdfManager.exe` を任意のフォルダに置いて起動します。
2. 初回は「新しいライブラリを作る」で、PDF を保存する場所を選びます。
3. PDF を画面にドラッグ&ドロップすると取り込み画面が開くので、タイトルやタグを入力して登録します。

データ(PDF・タグなど)はライブラリのフォルダ、アプリの設定は `%LOCALAPPDATA%\PdfManager` に保存されます。exe を新しいものに差し替えてもデータは消えません。

## 開発

### 必要なもの

- .NET 10 SDK

### ビルド・テスト・配布用 exe の作成

```powershell
dotnet build PdfManager.slnx                                   # ビルド
dotnet test tests/PdfManager.Core.Tests                         # テスト
dotnet run --project src/PdfManager.App                        # 起動
dotnet publish src/PdfManager.App -p:PublishProfile=SingleExe  # publish\PdfManager.exe を作成
```

性能確認用のダミーデータは `dotnet run tools/make-dummy-library.cs -- <作成先フォルダ> <件数>` で作れます。

### 構成

| フォルダ | 内容 |
|---|---|
| `src/PdfManager.App` | 画面(WPF、WPF-UI、CommunityToolkit.Mvvm) |
| `src/PdfManager.Core` | データベース(SQLite)、取り込み、検索、PDF・画像の処理(画面に依存しない) |
| `tests/PdfManager.Core.Tests` | Core の単体テスト(xUnit) |
| `tools` | 開発用のスクリプト |

主な部品:C# / .NET 10、WPF、SQLite(Windows 標準の winsqlite3.dll)、WebView2(PDF 表示)、Windows.Data.Pdf(サムネイル)、PdfPig(文字の読み取り・PDF 作成)、Windows.Media.Ocr(OCR)
