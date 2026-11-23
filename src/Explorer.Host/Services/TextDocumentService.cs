using System.Security.Cryptography;
using System.Text;
using Explorer.Core.Models;
using Explorer.Core.Security;

namespace Explorer.Host.Services;

public sealed class DocumentConflictException() : IOException("Die Datei wurde inzwischen in einem anderen Programm geändert. Lade sie neu, bevor du speicherst.");

public sealed class TextDocumentService(PathAccessPolicy policy)
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding[] BomEncodings =
    [
        new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
        new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
        new UTF8Encoding(true, true)
    ];
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(
        1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;

    public Task<TextDocumentDto> ReadAsync(string rawPath, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var path = policy.Authorize(rawPath);
        if (!File.Exists(path)) throw new FileNotFoundException();
        var info = new FileInfo(path);
        if (info.Length > MaximumBytes) throw new ArgumentException("Der Editor unterstützt Textdateien bis 2 MB. Lade größere Dateien herunter.");
        var bytes = ReadBytes(path);
        var (_, content) = Decode(bytes);
        return new TextDocumentDto(path, info.Name, content, MakeETag(bytes), new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }, cancellationToken);

    public async Task<TextDocumentDto> SaveAsync(TextSaveRequest request, CancellationToken cancellationToken)
    {
        if (request.Content is null || request.Content.Length > MaximumBytes) throw new ArgumentException("Der Editor unterstützt Textdateien bis 2 MB.");
        if (request.ETag is null || request.ETag.Length != 64 || !request.ETag.All(Uri.IsHexDigit))
            throw new ArgumentException("Die Dateiversion für den Speichervorgang fehlt oder ist ungültig.");
        var path = policy.Authorize(request.Path, write: true);
        if (!File.Exists(path)) throw new FileNotFoundException();
        var originalAttributes = File.GetAttributes(path);
        if ((originalAttributes & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("Die Datei ist schreibgeschützt. Ändere zuerst das Attribut in den Eigenschaften.");

        var current = ReadBytes(path);
        if (!string.Equals(MakeETag(current), request.ETag, StringComparison.OrdinalIgnoreCase))
            throw new DocumentConflictException();
        var (encoding, originalText) = Decode(current);
        var content = PreserveLineEndings(originalText, request.Content);
        if (content == originalText) return await ReadAsync(path, cancellationToken);
        try
        {
            if (encoding.GetByteCount(content) + encoding.Preamble.Length > MaximumBytes)
                throw new ArgumentException("Der Editor unterstützt Textdateien bis 2 MB.");
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Der ursprüngliche Zeichensatz kann mindestens eines der neuen Zeichen nicht speichern.", exception);
        }

        var parent = Path.GetDirectoryName(path) ?? throw new IOException("Der Dateiordner ist ungültig.");
        var temporary = Path.Combine(parent, $".wme-edit-{Guid.NewGuid():N}.partial");
        try
        {
            await File.WriteAllTextAsync(temporary, content, encoding, cancellationToken);
            var latest = ReadBytes(path);
            if (!string.Equals(MakeETag(latest), request.ETag, StringComparison.OrdinalIgnoreCase))
                throw new DocumentConflictException();
            File.Replace(temporary, path, destinationBackupFileName: null);
            return await ReadAsync(path, cancellationToken);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private static string MakeETag(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new ArgumentException("Der Editor unterstützt Textdateien bis 2 MB.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static (Encoding Encoding, string Content) Decode(byte[] bytes)
    {
        foreach (var encoding in BomEncodings)
        {
            if (!bytes.AsSpan().StartsWith(encoding.Preamble)) continue;
            try { return (encoding, RequireText(encoding.GetString(bytes.AsSpan(encoding.Preamble.Length)))); }
            catch (DecoderFallbackException exception) { throw new ArgumentException("Die Datei enthält ungültige Zeichen für ihren Zeichensatz.", exception); }
        }
        try { return (Utf8, RequireText(Utf8.GetString(bytes))); }
        catch (DecoderFallbackException) { return (Windows1252, RequireText(Windows1252.GetString(bytes))); }
    }

    private static string RequireText(string text)
    {
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t' or '\f')))
            throw new ArgumentException("Die Datei enthält Binärdaten oder einen nicht unterstützten Zeichensatz.");
        return text;
    }

    private static string PreserveLineEndings(string original, string edited)
    {
        if (original == edited || original.ReplaceLineEndings("\n") == edited.ReplaceLineEndings("\n")) return original;
        var withoutCrLf = original.Replace("\r\n", "");
        var crlf = withoutCrLf.Length != original.Length;
        var cr = withoutCrLf.Contains('\r');
        var lf = withoutCrLf.Contains('\n');
        if ((crlf ? 1 : 0) + (cr ? 1 : 0) + (lf ? 1 : 0) > 1)
            throw new ArgumentException("Diese Datei hat gemischte Zeilenenden. Bearbeite sie am PC, um das Format zu erhalten.");
        return crlf ? edited.ReplaceLineEndings("\r\n") : cr ? edited.ReplaceLineEndings("\r") : lf ? edited.ReplaceLineEndings("\n") : edited;
    }
}
