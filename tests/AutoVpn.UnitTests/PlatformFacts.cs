namespace AutoVpn.UnitTests;
public sealed class LinuxOnlyFactAttribute : Xunit.FactAttribute
{
    public LinuxOnlyFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Linux shell fixture; managed child coverage runs on both platforms."; }
}
internal static class TestCorePins
{
    internal static string ExpectedHash => OperatingSystem.IsWindows()
        ? "beb9878924d7bd38c67176b441ab5e668cc378bfe751c8dc8fef3eb5aaf566d5"
        : "3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8";
}
