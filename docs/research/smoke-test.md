# Smoke test: статический патч MonoMod в сетевой 1.1.2 — работает

2026-11-23. Цель — проверить решение из [mod-delivery-verdict.md](mod-delivery-verdict.md)
до того, как писать мод: (1) пропатченная `Assembly-CSharp.dll` грузится в
Unity 4.6 под CrossOver, (2) новый компонент с `[RPC]` на объекте
`NetworkManager` получает сообщение клиента, (3) сервер отвечает клиенту.

## Итог: все три — да

| Проверка | Лог | Результат |
| --- | --- | --- |
| Мод загружен на сервере | `server-log`: `[SYABH-COOP] loaded: smoke v1` | ✔ |
| Мод загружен на клиенте | `client-log`: `[SYABH-COOP] loaded: smoke v1` | ✔ |
| Клиент → сервер (RPC нового компонента через `NetworkManager.networkView`) | клиент: `client: sending ping`; сервер: `server: ping from 1 version=smoke-1` | ✔ |
| Сервер → конкретный клиент | клиент: `client: got pong:smoke-1` | ✔ |
| Ошибки загрузки типов | `TypeLoadException`: сервер 0, клиент 0 | ✔ |

Код пробы — [src/SyabhCoop/Smoke.cs](../../src/SyabhCoop/Smoke.cs). Запуск — на
тестовой копии `run/mp` (копия `game/v1.5/x64/multiplayer`), установка Steam не
тронута. Сервер — те же аргументы, что у кнопки «как отдельный процесс»
(`-server -port 44466 …`), клиент — `-client -ip 127.0.0.1 -port 44466`; оба
через `wine` из CrossOver 26.3, бутылка `Steam`.

Строки `Cannot resolve facilitator address`, `sendto failed`,
`connection request to 0.0.0.0:50005 failed` + `NullReferenceException` в
`OnFailedToConnect` — есть и без мода (лог игроков и наш запуск с
оригинальной DLL): NAT и мастер-серверы, к моду отношения не имеют.

## Что сломалось по дороге и как починено

**MonoMod 22.7 ломает сборку для старого Mono Unity 4.6.** Первый запуск с
патчем — 5 060 `TypeLoadException` (`Could not load type 'UnityEngine.Vector3'`,
`SpriteRoot`, …), с оригиналом — 0. Разделили опытами (сервер, 40 с):

| Сборка | TypeLoadException |
| --- | --- |
| оригинал | 0 |
| MonoMod без нашего патча («пустой» прогон) | 16 169 |
| чистая пересохранка Mono.Cecil 0.9.6 | 0 |
| чистая пересохранка Mono.Cecil 0.11.4 | 0 |
| «пустой» MonoMod + нормализация | 0 |

Причина: MonoMod переписывает обращения к **своим же** полям и методам сборки
как `MemberRef` с родителем `TypeDef` (48 523 ссылки против 9 687 у чистой
пересохранки) и добавляет тип `MonoMod.WasHere`. Старый Mono этого не переносит.
Решение — шаг нормализации [tools/cecil_rt/normalize.cs](../../tools/cecil_rt/normalize.cs)
после MonoMod: такие ссылки заменяются прямыми определениями (≈50 000 замен),
`MonoMod.WasHere` удаляется. Встроен в [tools/build_mod.sh](../../tools/build_mod.sh).

Мелочи: MonoMod тянет свои зависимости, если патч ссылается на `MonoMod.exe` —
поэтому патч на MonoMod не ссылается (префиксы `patch_` / `orig_` он узнаёт по
имени). Процесс под wine называется Windows-путём (`Y:\…\run\mp\sir.exe`) —
останавливать по нему (`pgrep -f 'syabh-coop.run.mp.sir.exe'`).

## Что не проверено

- Два разных компьютера по локальной сети (проверяли сервер и клиент на одном Mac).
- Игровая сцена: клиент дошёл до лобби, в игру не заходили — для пробы
  обмена сообщениями не нужно.
- Долгая игра, смена карты с модом.
