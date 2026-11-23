#!/bin/zsh
# Установка кооп-мода SYABH (двойной щелчок). Ищет сетевую версию игры в
# бутылках CrossOver, сохраняет НАСТОЯЩИЙ оригинал (проверка по SHA-256) и
# собирает мод из него на месте: в архиве нет кода игры, только код мода
# (Assembly-CSharp.SyabhCoop.mm.dll) и MonoMod — они под .NET CrossOver
# (wine-mono) вшивают мод в оригинал, как tools/build_mod.sh. Результат сверяется
# с mod.sha256 — тот же файл, что у автора. Откат — restore.command рядом.
# SYABH_BOTTLES — только для теста: искать в указанной папке вместо настоящих бутылок.
set -u
cd "${0:A:h}"
fail() { echo; echo "ОШИБКА: $1"; echo "Ничего не поменяла (или вернула как было). Нажмите Enter."; read -r; exit 1; }
sha() { shasum -a 256 "$1" | cut -d' ' -f1; }
is_mod() { grep -q -a "CoopCampaign" "$1"; }

mm="$PWD/Assembly-CSharp.SyabhCoop.mm.dll"
[[ -f $mm && -f VERSION.txt && -f base.sha256 && -f mod.sha256 && -f patcher/MonoMod.exe && -f patcher/normalize.exe ]] \
  || fail "рядом со скриптом нет файлов мода (Assembly-CSharp.SyabhCoop.mm.dll, patcher/, *.sha256) — распакуйте архив целиком."
ver=$(<VERSION.txt); orig_sha=$(cut -d' ' -f1 base.sha256); mod_sha=$(cut -d' ' -f1 mod.sha256)
cx=""
for a in /Applications/CrossOver.app "$HOME/Applications/CrossOver.app"; do
  [[ -x "$a/Contents/SharedSupport/CrossOver/bin/wine" ]] && { cx="$a/Contents/SharedSupport/CrossOver/bin/wine"; break; }
done
[[ -n $cx ]] || fail "не нашла CrossOver.app (ни в /Applications, ни в ~/Applications)."

rel="drive_c/Program Files (x86)/Steam/steamapps/common/SirYouAreBeingHunted/x64/multiplayer/sir_Data/Managed"
setopt nullglob
if [[ -n ${SYABH_BOTTLES:-} ]]; then bottles=("$SYABH_BOTTLES"/*); else bottles=("$HOME/Library/Application Support/CrossOver/Bottles"/* /Volumes/*/CrossOver/Bottles/*); fi
found=()
# одна бутылка может быть видна по двум путям (~/Library/…/Bottles — ссылка на диск): по настоящему пути
for b in "${bottles[@]}"; do b=${b:A}; [[ -f "$b/$rel/Assembly-CSharp.dll" ]] && (( ! ${found[(Ie)$b/$rel]} )) && found+=("$b/$rel"); done
if (( ${#found} == 0 )); then
  echo "Не нашла игру в бутылках CrossOver."
  echo "Перетащите сюда папку x64/multiplayer/sir_Data/Managed и нажмите Enter:"
  read -r d; d=${(Q)d}; d=${d%/}
  [[ -f "$d/Assembly-CSharp.dll" ]] || fail "в этой папке нет Assembly-CSharp.dll."
  found=("$d")
fi
if (( ${#found} > 1 )); then
  echo "Нашла несколько установок:"; for i in {1..${#found}}; do echo "  $i) ${found[$i]}"; done
  echo "Номер?"; read -r n
  [[ $n == <-> ]] && (( n >= 1 && n <= ${#found} )) || fail "нет такого номера."
  found=("${found[$n]}")
fi
dir=${found[1]}; cur="$dir/Assembly-CSharp.dll"; bak="$dir/Assembly-CSharp.dll.coop-original"
echo "Папка игры: $dir"
pgrep -f 'multiplayer.sir.exe' >/dev/null && fail "игра или сервер сейчас запущены — закройте их и запустите установку снова."
[[ -w $dir ]] || fail "нет прав на запись в папку игры."

# 1. Резерв оригинала: только файл с хешем оригинала MP 1.1.2.
if [[ -f $bak ]]; then
  [[ $(sha "$bak") == $orig_sha ]] || fail "сохранённый ранее Assembly-CSharp.dll.coop-original — не оригинал этой версии игры. Проверьте файлы игры в Steam (Свойства → Установленные файлы → Проверить целостность) и запустите установку снова."
  echo "Оригинал уже сохранён раньше и он настоящий — не трогаю."
elif [[ $(sha "$cur") == $orig_sha ]]; then
  cp "$cur" "$bak.tmp" && [[ $(sha "$bak.tmp") == $orig_sha ]] && mv "$bak.tmp" "$bak" \
    || { rm -f "$bak.tmp"; fail "не удалось сохранить оригинал (место на диске? права?)."; }
  echo "Оригинал сохранён: Assembly-CSharp.dll.coop-original"
elif is_mod "$cur"; then
  fail "в игре уже стоит мод, а сохранённого оригинала нет. Проверьте файлы игры в Steam (Проверить целостность) и запустите установку снова."
else
  fail "Assembly-CSharp.dll в игре — не та версия, под которую собран мод (другая версия игры?). Мод не поставлен."
fi

# 2. Собрать мод из оригинала — в бутылке игры, по короткому пути (длинные пути
#    Windows не берёт): C:\syabh-coop-build. Нужны все DLL игры (ссылки).
[[ $dir == */drive_c/* ]] || fail "папка игры не внутри бутылки CrossOver — собрать мод негде."
bottle=${dir%%/drive_c/*}
w="$bottle/drive_c/syabh-coop-build"; log="$w/build-log.txt"
rm -rf "$w"; mkdir -p "$w/work" "$w/patcher" || fail "не удалось создать папку сборки в бутылке."
cp "$dir"/*.dll "$w/work/" && cp "$bak" "$w/work/Assembly-CSharp.dll" && cp "$mm" "$w/work/" && cp patcher/* "$w/patcher/" \
  || fail "не удалось скопировать файлы для сборки."
echo "Собираю мод под вашу копию игры (до минуты)…"
wn() { (cd "$w/work" && MONOMOD_DEBUG_FORMAT=mdb CX_BOTTLE_PATH="${bottle:h}" "$cx" --bottle "${bottle:t}" --workdir 'C:\syabh-coop-build\work' "$@") >>"$log" 2>&1; }
wn 'C:\syabh-coop-build\patcher\MonoMod.exe' Assembly-CSharp.dll Assembly-CSharp.monomod.dll \
  && wn 'C:\syabh-coop-build\patcher\normalize.exe' Assembly-CSharp.monomod.dll Assembly-CSharp.coop.dll \
  || fail "сборка мода не удалась, журнал: $log"
built="$w/work/Assembly-CSharp.coop.dll"
[[ -f $built && $(sha "$built") == $mod_sha ]] || fail "собранный мод не совпал с эталоном (mod.sha256), журнал: $log"

# 3. Мод: через временный файл и переименование, чтобы обрыв не оставил половину DLL.
cp "$built" "$cur.coop-tmp" && [[ $(sha "$cur.coop-tmp") == $mod_sha ]] && mv "$cur.coop-tmp" "$cur" \
  || { rm -f "$cur.coop-tmp"; fail "не удалось записать мод в папку игры."; }
rm -rf "$w"
echo
echo "Мод установлен: версия $ver."
echo "Проверка — подключение к серверу с модом: без мода или с другой версией он не пустит."
echo "Нажмите Enter, чтобы закрыть."; read -r
exit 0
