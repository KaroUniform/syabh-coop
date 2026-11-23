// Патч: к строке версии игры дописываем версию мода.
// Игра при подключении сверяет эту строку (NetworkManager.SendClientVersion,
// CheckClientVersion = true) и отключает несовпадающих с причиной BadClientVersion —
// значит, без мода / с другой версией мода к серверу с модом не подключиться,
// и наоборот. Надпись в углу меню («Version 1.1.2 MP») — отдельный текст, её это не меняет
// (проверено 2026-11-23: мод загружен, в углу по-прежнему 1.1.2 MP).
#pragma warning disable CS0626, CS0108

class patch_WatermarkUpdater : WatermarkUpdater
{
    public static extern string orig_get_VersionNumber();

    public static new string VersionNumber
    {
        get { return orig_get_VersionNumber() + "+coop-" + CoopMod.Version; }
    }
}
