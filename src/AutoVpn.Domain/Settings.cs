namespace AutoVpn.Domain;

public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2,
}

public sealed record ProductSettings
{
    public int SchemaVersion { get; init; } = ProductLimits.SettingsSchemaVersion;
    public int Revision { get; init; } = 1;
    public bool DisclosureAccepted { get; init; }
    public int RefreshIntervalMinutes { get; init; } = ProductLimits.RefreshIntervalMinutes;
    public int RefreshJitterMinutes { get; init; } = ProductLimits.RefreshJitterMinutes;
    public int MaxAcceptableLatencyMs { get; init; } = ProductLimits.MaxAcceptableLatencyMs;
    public bool AllowInsecureCertificates { get; init; }
    public bool LanAccess { get; init; } = true;
    public bool ProtectionOnConnect { get; init; } = true;
    public bool StartAtLogin { get; init; }
    public bool AutoConnect { get; init; }
    public bool AutomaticSpeedTests { get; init; }
    public SelectionMode SelectionMode { get; init; } = SelectionMode.Automatic;
    public CountryConstraint CountryMode { get; init; } = CountryConstraint.Any;
    public string? Country { get; init; }
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public bool CloseToTray { get; init; } = true;
    public RankMode RankMode { get; init; } = RankMode.Stability;
    public IReadOnlyList<string> DisabledFamilyIds { get; init; } = [];

    public string? Validate()
    {
        if (SchemaVersion > ProductLimits.SettingsSchemaVersion)
        {
            return "Настройки созданы более новой версией AutoVPN. Откат не выполняется.";
        }

        if (RefreshIntervalMinutes < ProductLimits.MinimumRefreshIntervalMinutes || RefreshIntervalMinutes > 7 * 24 * 60)
        {
            return "Интервал обновления должен быть от 15 минут до 7 суток.";
        }

        if (RefreshJitterMinutes < 0 || RefreshJitterMinutes > RefreshIntervalMinutes)
        {
            return "Разброс расписания должен быть меньше интервала обновления.";
        }

        if (MaxAcceptableLatencyMs is < 50 or > 60_000)
        {
            return "Допустимая задержка вне диапазона 50–60000 мс.";
        }

        if (CountryMode == CountryConstraint.Strict && string.IsNullOrWhiteSpace(Country))
        {
            return "Для строгого фильтра страны нужно указать страну.";
        }

        if (!Enum.IsDefined(CountryMode) || !Enum.IsDefined(SelectionMode) || !Enum.IsDefined(Theme) || !Enum.IsDefined(RankMode))
        {
            return "Настройки содержат неизвестное значение перечисления.";
        }

        return null;
    }
}
