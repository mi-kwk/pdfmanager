namespace PdfManager.Core.Storage;

public sealed class DataFolder(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string DatabasePath => Path.Combine(Root, "db", "pdfmanager.db");

    public string GetPdfPath(string documentId) => Path.Combine(Root, "pdf", documentId[..2], documentId + ".pdf");

    public string GetThumbnailPath(string documentId) => Path.Combine(Root, "thumbs", documentId[..2], documentId + ".jpg");

    public void EnsureCreated()
    {
        foreach (var name in new[] { "db", "pdf", "thumbs" })
        {
            Directory.CreateDirectory(Path.Combine(Root, name));
        }
    }
}
