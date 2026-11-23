#!/bin/zsh
# Сборка мода: компилирует src/SyabhCoop/**/*.cs против ОРИГИНАЛЬНЫХ DLL сетевой
# 1.1.2 и вливает результат в Assembly-CSharp.dll статическим патчем MonoMod.
#   tools/build_mod.sh        → build/out/Assembly-CSharp.dll
# Оригиналы берутся из game/v1.5 (только чтение), сборка — в build/ (git ignore).
set -eu
root=${0:A:h:h}
managed="$root/game/v1.5/x64/multiplayer/sir_Data/Managed"
work="$root/build/work"; out="$root/build/out"
base_sha=$(cut -d' ' -f1 "$root/tools/base.sha256")

# 0. Патчим только известный оригинал MP 1.1.2 (ревью этапа 0): другой файл —
#    другая версия игры или уже пропатченный — ошибка, без сборки.
have=$(shasum -a 256 "$managed/Assembly-CSharp.dll" | cut -d' ' -f1)
if [[ $have != $base_sha ]]; then
  echo "ОШИБКА: $managed/Assembly-CSharp.dll — не оригинал MP 1.1.2" >&2
  echo "  ожидался $base_sha, а это $have (tools/base.sha256)" >&2
  exit 1
fi

rm -rf "$work" "$out"; mkdir -p "$work" "$out"
cp "$managed"/*.dll "$work"/
chmod -R u+w "$work"

# 1. Патч-сборка: имя <цель>.<мод>.mm.dll — так MonoMod её находит.
#    -nostdlib + mscorlib игры (2.0.5.0, профиль Unity), чтобы не утянуть ссылки
#    на чужой .NET. Исходники — все .cs под src/SyabhCoop, рекурсивно.
#    CS0626/0108/0114 — из-за патч-классов (orig_, скрытие членов).
srcs=("$root"/src/SyabhCoop/**/*.cs(N))
(( ${#srcs} > 0 )) || { echo "нет исходников" >&2; exit 1; }
mcs -target:library -nostdlib -noconfig -langversion:7 -nowarn:626,108,114 \
  -out:"$work/Assembly-CSharp.SyabhCoop.mm.dll" \
  -r:"$work/mscorlib.dll" -r:"$work/System.dll" -r:"$work/System.Core.dll" \
  -r:"$work/UnityEngine.dll" -r:"$work/Assembly-CSharp.dll" \
  -r:"$work/Assembly-CSharp-firstpass.dll" \
  "${srcs[@]}"

# 2. Статический патч: MonoMod ищет Assembly-CSharp.*.mm.dll рядом с целью.
(cd "$work" && mono "$root/tools/monomod/bin/MonoMod.exe" Assembly-CSharp.dll \
  "$work/Assembly-CSharp.monomod.dll")

# 3. Нормализация для старого Mono Unity 4.6: MonoMod пишет обращения к своим
#    же полям/методам как MemberRef с родителем TypeDef — Unity 4.6 на этом
#    падает (тысячи TypeLoadException; чистая пересохранка Cecil — без ошибок).
#    Заменяем их прямыми определениями, убираем MonoMod.WasHere.
#    Разбор — docs/research/smoke-test.md. Утилита собирается заново при каждой
#    сборке (ревью: иначе правки normalize.cs молча не применялись).
#    Отдельная папка: рядом с DLL игры mono подхватил бы урезанный System.Core
#    игры вместо своего (MissingMethodException в Linq).
nz="$root/build/tools"; rm -rf "$nz"; mkdir -p "$nz"
cp "$root/tools/monomod/dl/mono.cecil/lib/net40/Mono.Cecil.dll" "$nz/Mono.Cecil.dll"
mcs -langversion:7 -out:"$nz/normalize.exe" -r:"$nz/Mono.Cecil.dll" "$root/tools/cecil_rt/normalize.cs"
(cd "$work" && mono "$nz/normalize.exe" Assembly-CSharp.monomod.dll "$out/Assembly-CSharp.dll")
echo "готово: $out/Assembly-CSharp.dll ($(wc -c <"$out/Assembly-CSharp.dll" | tr -d ' ') байт)"
