using System.Globalization;
using PdfManager.Core.Catalog;
using PdfManager.Core.Documents;
using PdfManager.Core.Import;

namespace PdfManager.App.ViewModels;

// 詳細エリアで編集する、登録済みの 1 文書
public sealed class DetailItem : EditableDocument
{
    public DetailItem(DocumentDetail detail, IReadOnlyList<FieldDefinition> fields, Func<long, string?> tagKeyOf)
    {
        Detail = detail;
        Title = detail.Title;
        Memo = detail.Memo;
        Rating = detail.Rating;
        StoredFileName = detail.FileName;
        UseTitleAsFileName = detail.FileNameSource == FileNameSource.Title;
        Suffix = detail.FileNameSuffix;
        Prefix = detail.FileNameSource switch
        {
            FileNameSource.Tag when detail.FileNameSourceId is { } tagId && tagKeyOf(tagId) is { } key
                => new FileNamePrefixChoice(FileNamePrefixChoice.TagKind, key),
            FileNameSource.Field when detail.FileNameSourceId is { } fieldId
                => new FileNamePrefixChoice(FileNamePrefixChoice.FieldKind, fieldId.ToString(CultureInfo.InvariantCulture)),
            _ => null,
        };
        foreach (var (fieldId, value) in detail.FieldValues)
        {
            if (fields.FirstOrDefault(f => f.Id == fieldId) is { } field)
            {
                FieldInputs[fieldId] = FieldValues.Format(field.Type, value);
            }
        }
        foreach (var key in detail.TagIds.Select(tagKeyOf).OfType<string>())
        {
            TagKeys.Add(key);
        }
    }

    public DocumentDetail Detail { get; }

    public override string DisplayName => Detail.Title;

    public override string OriginalFileName => Detail.OriginalFileName;
}
