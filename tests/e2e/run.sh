#!/usr/bin/env bash
# End-to-end: pack Tactica, build the Shop sample with the package, accept its findings with
# "tactica baseline", then check that the build reports only a new mistake.
#
#   tests/e2e/run.sh                 uses nuget.org for the analyzer's build dependencies
#   E2E_PACK_ARGS="-p:TacticaOfflineRoslyn=true" E2E_NUGET_CONFIG=/path/nuget.config tests/e2e/run.sh   offline
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
trap '[ -n "${E2E_KEEP:-}" ] || rm -rf "$work"' EXIT
version="0.0.0-e2e.$(date +%s)"
feed="$work/feed"
pass=0
# Windows (Git Bash): paths written into files must be native.
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }

check() { # name, command...
  local name="$1"; shift
  if "$@"; then echo "  ok    $name"; pass=$((pass + 1)); else echo "  FAIL  $name"; cat "$work/build.log" 2>/dev/null | grep -E "TAC|error" | head -20; exit 1; fi
}

pack_args=(-c Release -o "$feed" -p:Version="$version" ${E2E_PACK_ARGS:-})
if [ -n "${E2E_NUGET_CONFIG:-}" ]; then pack_args+=(--configfile "$E2E_NUGET_CONFIG"); fi
dotnet pack "$root/src/Tactica" "${pack_args[@]}" > "$work/pack.log" 2>&1 || { cat "$work/pack.log"; exit 1; }
dotnet pack "$root/src/Tactica.Tool" "${pack_args[@]}" > "$work/pack-tool.log" 2>&1 || { cat "$work/pack-tool.log"; exit 1; }

# The sample, as a user's repository.
repo="$work/shop"
mkdir -p "$repo"
cp -r "$root/samples/Shop/." "$repo/"
git -C "$repo" init -q
echo "<Project><PropertyGroup><TacticaVersion>$version</TacticaVersion></PropertyGroup></Project>" > "$repo/Directory.Build.props"
{
  echo '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources>'
  echo "<add key=\"e2e\" value=\"$(native "$feed")\" />"
  echo '</packageSources></configuration>'
} > "$repo/nuget.config"
if [ -n "${E2E_NUGET_CONFIG:-}" ]; then
  # Offline: add the sources of the given config after the e2e feed.
  grep -o '<add key="[^"]*" value="[^"]*"' "$E2E_NUGET_CONFIG" | sed 's/$/ \/>/' > "$work/sources"
  sed -i "s#</packageSources>#$(sed 's/[#&]/\\&/g' "$work/sources" | tr -d '\n')</packageSources>#" "$repo/nuget.config"
fi

build() { dotnet build "$repo" --no-incremental 2>&1 | tee "$work/build.log" >/dev/null; }
rules() { grep -oE 'warning TAC[0-9]{3}' "$work/build.log" | sort -u | sed 's/warning //' | tr '\n' ' ' | sed 's/ $//'; }

echo "Tactica end-to-end ($version)"

build
expected="TAC001 TAC002 TAC003 TAC010 TAC012 TAC020 TAC021 TAC030"
actual="$(rules)"
check "the sample's mistakes are reported: $actual" eval '[ "$actual" = "$expected" ]'
check "the well-formed Orders model has no findings" eval '! grep -q "Orders/Order.cs(" "$work/build.log"'
check "messages point to the configured example" eval 'grep -q "Follow the pattern in samples/Shop/Orders/Order.cs" "$work/build.log"'

# The code fixes, from the command line: dotnet format applies every mechanical fix; the design findings remain.
fixed="$work/shop-fixed"
cp -r "$repo" "$fixed"
rm -rf "$fixed/bin" "$fixed/obj"   # the copied restore output points at the other folder
dotnet format analyzers "$fixed" --diagnostics TAC001 TAC002 TAC005 TAC010 TAC012 TAC013 TAC030 --severity warn -v diag > "$work/format.log" 2>&1 || { cat "$work/format.log"; exit 1; }
check "dotnet format applies every fix it was asked for" eval '! grep -q "Unable to fix" "$work/format.log"'
dotnet build "$fixed" --no-incremental > "$work/build.log" 2>&1 || { grep -E "error" "$work/build.log" | head -20; echo "  FAIL  the fixed sample does not compile"; exit 1; }
if [ "$(rules)" != "TAC003 TAC020 TAC021" ]; then echo "  --- dotnet format log"; grep -vE '^[[:space:]]*$' "$work/format.log" | grep -v "warning TAC" | tail -60 | sed 's/^/  | /'; fi
check "the fixed sample compiles and only the design findings remain: $(rules)" eval '[ "$(rules)" = "TAC003 TAC020 TAC021" ]'
check "aggregate state is private and collections are read-only" eval 'grep -q "public decimal Total { get; private set; }" "$fixed/Billing/Invoice.cs" && grep -q "public IReadOnlyList<string> Payments => _payments;" "$fixed/Billing/Invoice.cs"'

dotnet tool install Tactica.Tool --version "$version" --tool-path "$work/tools" --add-source "$feed" > "$work/tool.log" 2>&1 || { cat "$work/tool.log"; exit 1; }
(cd "$repo" && "$work/tools/tactica" baseline > "$work/baseline.log" 2>&1) || { cat "$work/baseline.log"; exit 1; }
check "tactica baseline writes tactica.baseline at the repository root" eval '[ -f "$repo/tactica.baseline" ]'
check "the baseline holds the 8 findings" eval '[ "$(grep -c '\''^TAC'\'' "$repo/tactica.baseline")" = "8" ]'

build
check "the build reports nothing once the findings are accepted" eval '[ -z "$(rules)" ]'

sed -i 's/public string Product { get; private set; }/public string Product { get; set; }/' "$repo/Orders/Order.cs"
build
check "a new mistake is still reported" eval '[ "$(rules)" = "TAC001" ] && grep -q "OrderLine.Product has a public set" "$work/build.log"'

echo "$pass passed"
