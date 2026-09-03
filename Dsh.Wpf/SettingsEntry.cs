namespace Dsh.Wpf;

/// <summary>One settings namespace row for the read-only settings panel.</summary>
public sealed record SettingsEntry(string Ns, string Applies, long Revision, string ValuePreview);
