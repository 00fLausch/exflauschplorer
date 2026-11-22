using System.Security.Cryptography;
using System.Text;
using Explorer.Core.Models;
using Explorer.Core.Security;

namespace Explorer.Host.Services;

public sealed class DocumentConflictException() : IOException("Die Datei wurde inzwischen in einem anderen Programm geändert. Lade sie neu, bevor du speicherst.");

public sealed class TextDocumentService(PathAccessPolicy policy)
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public Task<TextDocumentDto> ReadAsync(string rawPath, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var path = policy.Authorize(rawPath);
        if (!File.Exists(path)) throw new FileNotFoundException();
        var info = new FileInfo(path);
        if (info.Length > MaximumBytes) throw new ArgumentException("Der Editor unterstützt Textdateien bis 2 MB. Lade größere Dateien herunter.");
        var bytes = File.ReadAllBytes(path);
        var content = Decode(bytes);
        return new TextDocumentDto(path, info.Name, content, MakeETag(bytes), new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }, cancellationToken);

    public async Task<TextDocumentDto> SaveAsync(TextSaveRequest request, CancellationToken cancellationToken)
    {
        if (request.Content.Length > MaximumBytes) throw new ArgumentException("Der Editor unterstützt Textdateien bis 2 MB.");
        var path = policy.Authorize(request.Path, write: true);
        if (!File.Exists(path)) throw new FileNotFoundException();
        var originalAttributes = File.GetAttributes(path);
        if ((originalAttributes & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("Die Datei ist schreibgeschützt. Ändere zuerst das Attribut in den Eigenschaften.");

        var current = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(MakeETag(current)), Encoding.ASCII.GetBytes(request.ETag)))
            throw new DocumentConflictException();

        var parent = Path.GetDirectoryName(path) ?? throw new IOException("Der Dateiordner ist ungültig.");
        var temporary = Path.Combine(parent, $".wme-edit-{Guid.NewGuid():N}.partial");
        try
        {
            await File.WriteAllTextAsync(temporary, request.Content, Utf8, cancellationToken);
            File.SetAttributes(temporary, originalAttributes | FileAttributes.Hidden);
            var latest = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(MakeETag(latest)), Encoding.ASCII.GetBytes(request.ETag)))
                throw new DocumentConflictException();
            File.Move(temporary, path, overwrite: true);
            File.SetAttributes(path, originalAttributes);
            return await ReadAsync(path, cancellationToken);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private static string MakeETag(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) return Utf8.GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length));
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble)) return Encoding.Unicode.GetString(bytes.AsSpan(Encoding.Unicode.Preamble.Length));
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble)) return Encoding.BigEndianUnicode.GetString(bytes.AsSpan(Encoding.BigEndianUnicode.Preamble.Length));
        return Utf8.GetString(bytes);
    }
}
