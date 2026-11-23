#!/bin/zsh
# Автопроверка мода на одном Mac: выделенный сервер + 0..2 клиента под CrossOver
# wine, клиенты подключаются по адресу Mac в сети (en0), а не 127.0.0.1.
#
#   tools/test_coop.sh [ключи]
#     -c N        клиентов: 0, 1 или 2 (по умолчанию 2)
#     -d DLL      DLL для всех сторон (по умолчанию build/out/Assembly-CSharp.dll)
#     -s/-a/-b DLL  своя DLL у сервера / клиента A / клиента B
#     -t СЕК      сколько ждать после входа клиентов (60); раньше — если все -e выполнены или есть -x
#     -n МЕТКА    имя прогона (логи run/logs/<метка>-{server,a,b}.txt)
#     -S "флаги"  доп. флаги серверу (ставятся ДО -servername: тот съедает остаток строки)
#     -A/-B "флаги"  доп. флаги клиенту A / B (например -coopbot)
#     -e сторона:regex   строка ОБЯЗАНА быть в логе стороны (server|a|b), можно много раз
#     -x сторона:regex   строки НЕ должно быть
#
# Выход 0 — все ожидания выполнены, сервер поднялся, логи есть, TypeLoadException нет.
# Копии игры: run/mp (сервер), run/mp-client, run/mp-client2 (клоны APFS).
# Процессы останавливаются в конце и при прерывании. Установка Steam не трогается.
# CrossOver: COOP_CX_APP (по умолчанию ~/Applications или /Applications/CrossOver.app),
# бутылка — COOP_BOTTLES (папка бутылок) и COOP_BOTTLE (имя, Steam).
set -u
root=${0:A:h:h}
cxapp=${COOP_CX_APP:-}
[[ -z $cxapp ]] && for a in "$HOME/Applications/CrossOver.app" /Applications/CrossOver.app; do [[ -d $a ]] && { cxapp=$a; break; }; done
X="$cxapp/Contents/SharedSupport/CrossOver"
bottles=${COOP_BOTTLES:-$HOME/Library/Application Support/CrossOver/Bottles}; bottles=${bottles:A}
clients=2; dll="$root/build/out/Assembly-CSharp.dll"; sdll=""; adll=""; bdll=""
secs=60; tag=$(date +%H%M%S); sflags=""; aflags=""; bflags=""; expect=(); forbid=()
while getopts "c:d:s:a:b:t:n:S:A:B:e:x:" o; do
  case $o in
    c) clients=$OPTARG;; d) dll=${OPTARG:A};; s) sdll=${OPTARG:A};; a) adll=${OPTARG:A};; b) bdll=${OPTARG:A};;
    t) secs=$OPTARG;; n) tag=$OPTARG;; S) sflags=$OPTARG;; A) aflags=$OPTARG;; B) bflags=$OPTARG;;
    e) expect+=("$OPTARG");; x) forbid+=("$OPTARG");;
    *) echo "неизвестный ключ" >&2; exit 2;;
  esac
done
: ${sdll:=$dll} ${adll:=$dll} ${bdll:=$dll}
S="$root/run/mp"; A="$root/run/mp-client"; B="$root/run/mp-client2"; L="$root/run/logs"
mkdir -p "$L"
die() { echo "ПРОВАЛ: $1" >&2; stop; exit 1; }
# процессы под wine называются Windows-путём (Y:\…\run\mp…\sir.exe) — ищем только тестовые копии
stop() {
  local p; p=$(pgrep -f 'syabh-coop.run.mp(-client2?)?.sir.exe')
  [[ -n $p ]] && kill ${=p} 2>/dev/null
  for i in {1..10}; do pgrep -f 'syabh-coop.run.mp(-client2?)?.sir.exe' >/dev/null || return 0; sleep 1; done
  p=$(pgrep -f 'syabh-coop.run.mp(-client2?)?.sir.exe'); [[ -n $p ]] && kill -9 ${=p} 2>/dev/null; return 0
}
trap 'stop; exit 130' INT TERM
[[ -d $S ]] || die "нет $S — скопируйте game/v1.5/x64/multiplayer в run/mp"
for f in $sdll $adll $bdll; do [[ -f $f ]] || die "нет DLL $f"; done
(( clients >= 1 )) && { [[ -d $A ]] || cp -c -R "$S" "$A" || die "не удалось создать $A"; }
(( clients >= 2 )) && { [[ -d $B ]] || cp -c -R "$S" "$B" || die "не удалось создать $B"; }
ip=$(ipconfig getifaddr en0 2>/dev/null); [[ -n $ip ]] || ip=$(ipconfig getifaddr en1 2>/dev/null)
[[ -n $ip ]] || { ip=127.0.0.1; echo "ВНИМАНИЕ: нет адреса в сети, клиенты пойдут через 127.0.0.1"; }
stop
cp "$sdll" "$S/sir_Data/Managed/Assembly-CSharp.dll" || die "не скопировалась DLL сервера"
(( clients >= 1 )) && { cp "$adll" "$A/sir_Data/Managed/Assembly-CSharp.dll" || die "не скопировалась DLL клиента A"; }
(( clients >= 2 )) && { cp "$bdll" "$B/sir_Data/Managed/Assembly-CSharp.dll" || die "не скопировалась DLL клиента B"; }
rm -f "$S/server-log-44466.txt" "$A/client-log.txt" "$B/client-log.txt" "$L/$tag-"{server,a,b}.txt
[[ -x $X/bin/wine ]] || die "не нашла CrossOver ($X) — задайте COOP_CX_APP"
wine() { CX_BOTTLE_PATH="$bottles" "$X/bin/wine" --bottle "${COOP_BOTTLE:-Steam}" "$@"; }
win="-screen-fullscreen 0 -screen-height 450 -screen-width 640 -logmessages"
(cd "$S" && wine --workdir "$S" "$S/sir.exe" ${=win} -logFile ./server-log-44466.txt -server -port 44466 \
  -numpieces 12 -piecestowin 10 -maxplayers 4 -minplayers $(( clients > 0 ? clients : 1 )) ${=sflags} \
  -servername syabh-test >"$L/wine-$tag-server.txt" 2>&1 &)
ok=0
for i in {1..90}; do grep -q -E "SYABH-COOP\] .*server: initialized|REGISTERSERVER" "$S/server-log-44466.txt" 2>/dev/null && { ok=1; break; }; sleep 1; done
(( ok )) || { cp "$S/server-log-44466.txt" "$L/$tag-server.txt" 2>/dev/null; die "сервер не поднялся за 90 с (лог: run/logs/$tag-server.txt)"; }
sleep 3
client() { (cd "$1" && wine --workdir "$1" "$1/sir.exe" ${=win} -logFile ./client-log.txt \
  -client -ip "$ip" -port 44466 ${=3} >"$L/wine-$tag-$2.txt" 2>&1 &); }
(( clients >= 1 )) && client "$A" a "$aflags"
(( clients >= 2 )) && { sleep 5; client "$B" b "$bflags"; }
# ждём до $secs с, но заканчиваем раньше: все -e уже есть (успех) или
# появилось -x / исключение генерации (провал) — логи читаем на лету
logof() { case $1 in server) echo "$S/server-log-44466.txt";; a) echo "$A/client-log.txt";; b) echo "$B/client-log.txt";; esac; }
end=$(( SECONDS + secs ))
while (( SECONDS < end )); do
  sleep 5
  done_all=1
  for e in $expect; do grep -a -q -E "${e#*:}" "$(logof ${e%%:*})" 2>/dev/null || { done_all=0; break; }; done
  (( ${#expect} > 0 && done_all )) && { echo "все ожидания выполнены через $(( secs - (end - SECONDS) )) с"; sleep 3; break; }
  early=""
  for e in $forbid; do grep -a -q -E "${e#*:}" "$(logof ${e%%:*})" 2>/dev/null && { early="$e"; break; }; done
  [[ -z $early ]] && grep -a -q -E "IsolatedStorageException|TypeLoadException" "$S/server-log-44466.txt" 2>/dev/null && early="server:исключение"
  # процесс сервера пропал (упал или закрыт) — дальше ждать нечего
  [[ -z $early ]] && ! pgrep -f 'syabh-coop.run.mp.sir.exe' >/dev/null && early="server:процесс сервера завершился"
  [[ -n $early ]] && { echo "ранний останов: /$early/"; break; }
done
stop
cp "$S/server-log-44466.txt" "$L/$tag-server.txt" 2>/dev/null
(( clients >= 1 )) && cp "$A/client-log.txt" "$L/$tag-a.txt" 2>/dev/null
(( clients >= 2 )) && cp "$B/client-log.txt" "$L/$tag-b.txt" 2>/dev/null

sides=(server); (( clients >= 1 )) && sides+=(a); (( clients >= 2 )) && sides+=(b)
bad=0
echo "прогон $tag, сервер $ip:44466, клиентов $clients"
for side in $sides; do
  f="$L/$tag-$side.txt"
  if [[ ! -s $f ]]; then echo "== $side: ЛОГА НЕТ"; bad=1; continue; fi
  n=$(grep -a -c TypeLoadException "$f")
  ex=$(grep -a -c -E "Exception" "$f")
  echo "== $side: строк $(wc -l <"$f" | tr -d ' '), TypeLoadException $n, всего строк с Exception $ex"
  (( n > 0 )) && bad=1
  grep -a -E "SYABH-COOP\] .*(TEST (FAIL|DONE)|ERROR|WARN)|bad client version|Disconnecting|isconnected" "$f" | head -60 | cut -c1-220 | sed 's/^/   /'
done
for e in $expect; do
  side=${e%%:*}; re=${e#*:}
  if grep -a -q -E "$re" "$L/$tag-$side.txt" 2>/dev/null; then echo "  ✔ $side: /$re/"; else echo "  ✖ $side: нет /$re/"; bad=1; fi
done
for e in $forbid; do
  side=${e%%:*}; re=${e#*:}
  if grep -a -q -E "$re" "$L/$tag-$side.txt" 2>/dev/null; then echo "  ✖ $side: есть запрещённое /$re/"; bad=1; else echo "  ✔ $side: нет /$re/"; fi
done
pgrep -f 'syabh-coop.run.mp(-client2?)?.sir.exe' >/dev/null && { echo "ВНИМАНИЕ: процессы ещё живы"; bad=1; }
(( bad )) && { echo "ИТОГ: ПРОВАЛ"; exit 1; }
echo "ИТОГ: ОК"
