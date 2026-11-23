#!/bin/zsh
# Вернуть оригинальную Assembly-CSharp.dll (снять кооп-мод). Двойной щелчок.
# Возвращает только проверенный оригинал (SHA-256 из base.sha256).
# SYABH_BOTTLES — только для теста: искать в указанной папке вместо настоящих бутылок.
set -u
cd "${0:A:h}"
fail() { echo; echo "ОШИБКА: $1"; echo "Нажмите Enter."; read -r; exit 1; }
sha() { shasum -a 256 "$1" | cut -d' ' -f1; }
[[ -f base.sha256 ]] || fail "рядом со скриптом нет base.sha256 — распакуйте архив целиком."
orig_sha=$(cut -d' ' -f1 base.sha256)
pgrep -f 'multiplayer.sir.exe' >/dev/null && fail "игра или сервер сейчас запущены — закройте их и запустите откат снова."

rel="drive_c/Program Files (x86)/Steam/steamapps/common/SirYouAreBeingHunted/x64/multiplayer/sir_Data/Managed"
setopt nullglob
if [[ -n ${SYABH_BOTTLES:-} ]]; then bottles=("$SYABH_BOTTLES"/*); else bottles=("$HOME/Library/Application Support/CrossOver/Bottles"/* /Volumes/*/CrossOver/Bottles/*); fi
found=()
# одна бутылка может быть видна по двум путям (~/Library/…/Bottles — ссылка на диск): по настоящему пути
for b in "${bottles[@]}"; do b=${b:A}; [[ -f "$b/$rel/Assembly-CSharp.dll.coop-original" ]] && (( ! ${found[(Ie)$b/$rel]} )) && found+=("$b/$rel"); done
if (( ${#found} == 0 )); then
  echo "Не нашла сохранённого оригинала в бутылках CrossOver."
  echo "Если ставили мод вручную — перетащите сюда ту папку sir_Data/Managed и нажмите Enter (или просто Enter, чтобы выйти):"
  read -r d; d=${(Q)d}; d=${d%/}
  [[ -n $d && -f "$d/Assembly-CSharp.dll.coop-original" ]] || fail "сохранённого оригинала нет. Можно проверить файлы игры в Steam (Свойства → Установленные файлы → Проверить целостность)."
  found=("$d")
fi
if (( ${#found} > 1 )); then
  echo "Нашла несколько установок с модом:"; for i in {1..${#found}}; do echo "  $i) ${found[$i]}"; done
  echo "Номер?"; read -r n
  [[ $n == <-> ]] && (( n >= 1 && n <= ${#found} )) || fail "нет такого номера."
  found=("${found[$n]}")
fi
dir=${found[1]}; cur="$dir/Assembly-CSharp.dll"; bak="$dir/Assembly-CSharp.dll.coop-original"
[[ $(sha "$bak") == $orig_sha ]] || fail "сохранённый оригинал повреждён или от другой версии игры — не возвращаю его. Проверьте файлы игры в Steam (Проверить целостность)."
cp "$bak" "$cur.coop-tmp" && [[ $(sha "$cur.coop-tmp") == $orig_sha ]] && mv "$cur.coop-tmp" "$cur" \
  || { rm -f "$cur.coop-tmp"; fail "не удалось записать оригинал в папку игры."; }
echo "Оригинал возвращён: $dir"
echo "Нажмите Enter, чтобы закрыть."; read -r
exit 0
