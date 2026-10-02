namespace AutoVpn.Domain;

public static class Ru
{
    public const string AppName = "AutoVPN";
    public const string Connect = "Подключить";
    public const string Disconnect = "Отключить";
    public const string Connected = "Подключён";
    public const string Disconnected = "Не подключён";
    public const string Connecting = "Подключение";
    public const string Reconnecting = "Переподключение";
    public const string Checking = "Проверяется";
    public const string Working = "Работает";
    public const string NeedsCheck = "Нужна проверка";
    public const string Unavailable = "Недоступен";
    public const string Unsupported = "Не поддерживается";
    public const string PolicyBlocked = "Заблокирован настройками безопасности";
    public const string Retained = "Сохранён из предыдущего обновления";
    public const string ProtectionOn = "Защита при обрыве: включена";
    public const string ProtectionOff = "Защита при обрыве выключена. При сбое туннеля трафик может пойти напрямую.";
    public const string Refresh = "Обновить подписки";
    public const string RecheckSources = "Перепроверить источники";
    public const string Servers = "Серверы";
    public const string WorkingTab = "Рабочие";
    public const string Favorites = "Избранное";
    public const string All = "Все";
    public const string Subscriptions = "Подписки";
    public const string Settings = "Настройки";
    public const string Connection = "Подключение";
    public const string Latency = "Задержка";
    public const string LatencyHint = "Проверка HTTPS через сервер; это не ICMP-пинг";
    public const string SpeedUnknown = "Не измерена";
    public const string SpeedStale = "Устарела";
    public const string NoServer = "Нет рабочего сервера";
    public const string UpdateFailedKeep =
        "Не удалось обновить подписки. Подключение работает; сохранённые серверы не удалены. Повторить";
    public const string TorSeparate = "Tor-мосты — отдельный тип, не используется в этой версии";
    public const string Disclosure =
        "AutoVPN подключается к публичным серверам из стороннего списка. Их операторы не проверяются. " +
        "Поставщик подписки видит запросы на скачивание, а проверки серверов обращаются к публичным адресам. " +
        "Приложение не обещает анонимность, постоянную работу или доступ к любым сайтам. " +
        "Обычный HTTPS по-прежнему нужен: туннель не делает незашифрованный трафик приложений безопасным.";
    public const string AcceptDisclosure = "Понятно, продолжить";
    public const string PinServer = "Закрепить сервер: не переключать";
    public const string AutomaticStable = "Автоматически — стабильное соединение";
    public const string AllowLan = "Разрешить доступ к локальной сети";
    public const string AllowInsecure = "Разрешить серверы без проверки сертификата";
    public const string Exit = "Выйти";
    public const string ShowWindow = "Открыть окно";
    public const string RestoreNetwork = "Восстановить сеть";
    public const string AdvertisedCountry = "Страна по подписке";
    public const string ObservedExit = "Подтверждённый выход";
    public const string CheckedAgo = "Проверено";
    public const string NotGuaranteed = "Проверено недавно. Это не гарантия, что сервер всё ещё работает.";
    public const string ServiceMissing =
        "Служба Windows не подтвердила туннель. Подключение через TUN недоступно, пока проверка Windows не пройдена.";
    public const string WindowsGate =
        "Проверки TUN, DNS, IPv6, восстановления и установщика на Windows ещё не выполнялись.";
}
