namespace Explorer.Core.Security;

public static class GermanErrors
{
    public static string For(Exception exception) => exception switch
    {
        IOException io when io.Message.StartsWith("Die Datei wurde nicht gelöscht.", StringComparison.Ordinal) => io.Message,
        IOException io when io.Message.StartsWith("Die Datei wurde inzwischen", StringComparison.Ordinal) => io.Message,
        IOException io when io.Message.StartsWith("Die Quelldatei hat sich während", StringComparison.Ordinal) => io.Message,
        FileNotFoundException or DirectoryNotFoundException => "Die Datei oder der Ordner wurde nicht gefunden. Vielleicht wurde das Laufwerk getrennt.",
        UnauthorizedAccessException => "Windows hat den Zugriff verweigert. Prüfe die Freigabe und deine Berechtigungen.",
        PathTooLongException => "Der Pfad ist zu lang. Benenne einen Ordner kürzer oder verschiebe ihn näher zum Laufwerk.",
        TimeoutException => "Das Laufwerk antwortet nicht rechtzeitig. Prüfe die Verbindung und versuche es erneut.",
        IOException io when IsLocked(io) => "Die Datei wird gerade von einem anderen Programm verwendet.",
        IOException io when IsFull(io) => "Auf dem Datenträger ist nicht genug freier Speicherplatz.",
        IOException => "Windows konnte den Dateivorgang nicht abschließen. Prüfe Laufwerk und Berechtigungen.",
        ArgumentException => exception.Message,
        _ => "Der Vorgang konnte nicht abgeschlossen werden."
    };

    private static bool IsLocked(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static bool IsFull(IOException exception) =>
        (exception.HResult & 0xFFFF) is 39 or 112;
}
