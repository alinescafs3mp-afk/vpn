using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Persistence;

var path = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoVPN", "effects.sqlite");

if (!File.Exists(path))
{
    Console.WriteLine("Журнал эффектов не найден. Снимать нечего. Системная сеть не изменялась.");
    return 0;
}

using var journal = EffectJournal.Open(path);
var recovery = journal.Recover(new UnavailableNetworkGuard());
if (recovery.QuarantinePath is not null)
{
    Console.WriteLine("Журнал не прочитан и отложен: " + recovery.QuarantinePath);
    Console.WriteLine("Правила из него не считаются снятыми. Сеть не изменялась.");
    return 2;
}

if (recovery.Completed && recovery.OpenEffects == 0)
{
    Console.WriteLine(recovery.RemovedEffects == 0
        ? "Открытых своих правил нет. Системная сеть не изменялась."
        : "Открытые записи журнала отмечены снятыми.");
    return 0;
}

Console.WriteLine("Снятие своих правил не подтверждено. Причина: " + (recovery.ReasonCode ?? "UNKNOWN"));
Console.WriteLine("Осталось открытых записей: " + recovery.OpenEffects.ToString(System.Globalization.CultureInfo.InvariantCulture));
return 3;
