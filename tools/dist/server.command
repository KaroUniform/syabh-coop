#!/bin/zsh
# Запуск сервера кооп-кампании SYABH (двойной щелчок) — на том Mac, где сервер.
# Делает то же, что кнопка игры «Start Server» → отдельный процесс
# (C_MenuStartServer.StartServerAsInstance): sir.exe -server -port 44466 …,
# но без меню и с параметрами для двоих. Сервер — отдельное окно; закрыть окно =
# остановить сервер (кампания записывается при закрытии и раз в 30 с).
# Для теста: SYABH_GAME_DIR — папка x64/multiplayer, SYABH_BOTTLE — бутылка CrossOver.
set -u
cd "${0:A:h}"
fail() { echo; echo "ОШИБКА: $1"; echo "Нажмите Enter."; read -r; exit 1; }

rel="drive_c/Program Files (x86)/Steam/steamapps/common/SirYouAreBeingHunted/x64/multiplayer"
setopt nullglob
if [[ -n ${SYABH_GAME_DIR:-} ]]; then
  game=$SYABH_GAME_DIR; bottle=${SYABH_BOTTLE:?}
else
  found=()
  for b in "$HOME/Library/Application Support/CrossOver/Bottles"/* /Volumes/*/CrossOver/Bottles/*; do
    b=${b:A}   # одна бутылка может быть видна по двум путям (ссылка на диск)
    [[ -f "$b/$rel/sir.exe" ]] && (( ! ${found[(Ie)$b]} )) && found+=("$b")
  done
  (( ${#found} == 0 )) && fail "не нашла сетевую игру в бутылках CrossOver."
  if (( ${#found} > 1 )); then
    echo "Нашла несколько бутылок с игрой:"; for i in {1..${#found}}; do echo "  $i) ${found[$i]}"; done
    echo "Номер?"; read -r n
    [[ $n == <-> ]] && (( n >= 1 && n <= ${#found} )) || fail "нет такого номера."
    found=("${found[$n]}")
  fi
  bottle=${found[1]}; game="$bottle/$rel"
fi
cx=""
for a in /Applications/CrossOver.app "$HOME/Applications/CrossOver.app"; do
  [[ -x "$a/Contents/SharedSupport/CrossOver/bin/wine" ]] && { cx="$a/Contents/SharedSupport/CrossOver/bin/wine"; break; }
done
[[ -n $cx ]] || fail "не нашла CrossOver.app (ни в /Applications, ни в ~/Applications)."
grep -q -a "CoopCampaign" "$game/sir_Data/Managed/Assembly-CSharp.dll" || fail "в игре не стоит кооп-мод — сначала install.command."
pgrep -f 'sir\.exe.* -server ' >/dev/null && fail "сервер уже запущен (его окно открыто). Второй не нужен."

log="$game/server-log-44466.txt"
rm -f "$log"
echo "Запускаю сервер: $game"
(cd "$game" && CX_BOTTLE_PATH="${bottle:h}" "$cx" --bottle "${bottle:t}" --workdir "$game" "$game/sir.exe" \
  -screen-fullscreen 0 -screen-height 600 -screen-width 800 -logFile ./server-log-44466.txt \
  -server -port 44466 -numpieces 5 -piecestowin 25 -maxplayers 4 -minplayers 2 -servername syabh-coop \
  >/dev/null 2>&1 &)
for i in {1..120}; do
  grep -q -a "SYABH-COOP\] .*server: initialized" "$log" 2>/dev/null && break
  grep -q -a -E "SYABH-COOP\] .*ERROR|TypeLoadException" "$log" 2>/dev/null && fail "сервер не поднялся, журнал: $log"
  sleep 1
done
grep -q -a "SYABH-COOP\] .*server: initialized" "$log" 2>/dev/null || fail "сервер не поднялся за 2 минуты, журнал: $log"
ip=$(ipconfig getifaddr en0 2>/dev/null); [[ -n $ip ]] || ip=$(ipconfig getifaddr en1 2>/dev/null)
echo
echo "Сервер готов (порт 44466)."
echo "Адрес для подключения:  ${ip:-не нашла адрес в сети — посмотрите в Настройки → Wi-Fi → Подробнее}"
echo
echo "Оба: сетевая игра → Connect to Server → Server address: ${ip:-<адрес>} → Connect."
echo "Остров начнётся, когда подключатся двое. Остановить сервер — закрыть его окно."
echo "Это окно Терминала можно закрыть."
echo "Нажмите Enter."; read -r
exit 0
