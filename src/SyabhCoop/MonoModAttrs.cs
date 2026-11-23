// Атрибуты MonoMod, которые патчер узнаёт по полному имени типа: мод не
// ссылается на сборки MonoMod (см. smoke-test.md), поэтому объявляем их сами.
//   [MonoModIgnore] — член объявлен только чтобы сослаться на одноимённый
//   приватный член игры; патчер его не переносит, ссылки идут на оригинал.
//   (Без атрибута объявление без тела ЗАМЕНИЛО бы метод игры пустым — проверено.)
namespace MonoMod
{
    [System.AttributeUsage(System.AttributeTargets.All)]
    internal class MonoModIgnore : System.Attribute { }
}
