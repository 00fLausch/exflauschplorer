namespace Explorer.Core.Security;

public sealed record ShareRoot(string Path, bool ReadOnly = false, string? Label = null);
