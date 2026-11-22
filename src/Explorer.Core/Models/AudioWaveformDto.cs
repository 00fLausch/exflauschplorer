namespace Explorer.Core.Models;

public sealed record AudioWaveformDto(IReadOnlyList<double> Peaks, double Duration);
