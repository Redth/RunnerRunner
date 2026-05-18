#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
results_dir="$repo_root/TestResults/coverage"
line_threshold="${COVERAGE_LINE_THRESHOLD:-}"
branch_threshold="${COVERAGE_BRANCH_THRESHOLD:-}"
critical_line_threshold="${COVERAGE_CRITICAL_LINE_THRESHOLD:-}"
crap_threshold="${COVERAGE_CRAP_THRESHOLD:-}"

usage() {
  cat <<'EOF'
Usage: scripts/coverage.sh [results-dir] [options]

Options:
  --line-threshold PERCENT           Fail when total line coverage is below PERCENT.
  --branch-threshold PERCENT         Fail when total branch coverage is below PERCENT.
  --critical-line-threshold PERCENT  Fail when Server Services/Grains line coverage is below PERCENT.
  --crap-threshold SCORE             Fail when a critical Server Services/Grains method has CRAP above SCORE.
  -h, --help                         Show this help.

Thresholds are optional; by default this script reports coverage without failing on low coverage.
Environment defaults: COVERAGE_LINE_THRESHOLD, COVERAGE_BRANCH_THRESHOLD,
COVERAGE_CRITICAL_LINE_THRESHOLD, COVERAGE_CRAP_THRESHOLD.
EOF
}

if [[ $# -gt 0 && "$1" != --* ]]; then
  results_dir="$1"
  shift
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    --line-threshold)
      if [[ $# -lt 2 || "$2" == --* ]]; then echo "--line-threshold requires a value" >&2; exit 2; fi
      line_threshold="$2"
      shift 2
      ;;
    --branch-threshold)
      if [[ $# -lt 2 || "$2" == --* ]]; then echo "--branch-threshold requires a value" >&2; exit 2; fi
      branch_threshold="$2"
      shift 2
      ;;
    --critical-line-threshold)
      if [[ $# -lt 2 || "$2" == --* ]]; then echo "--critical-line-threshold requires a value" >&2; exit 2; fi
      critical_line_threshold="$2"
      shift 2
      ;;
    --crap-threshold)
      if [[ $# -lt 2 || "$2" == --* ]]; then echo "--crap-threshold requires a value" >&2; exit 2; fi
      crap_threshold="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

rm -rf "$results_dir"
mkdir -p "$results_dir/raw"

test_projects=()
while IFS= read -r project; do
  test_projects+=("$project")
done < <(find "$repo_root/tests" -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' | sort)

if [[ "${#test_projects[@]}" -eq 0 ]]; then
  echo "No test projects found under $repo_root/tests" >&2
  exit 1
fi

echo "Running ${#test_projects[@]} test project(s) with Coverlet coverage..."
for project in "${test_projects[@]}"; do
  echo "TEST_PROJECT:${project#$repo_root/}"
  dotnet test "$project" \
    --verbosity minimal \
    --collect:"XPlat Code Coverage" \
    --results-directory "$results_dir/raw" \
    -- \
    DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura \
    DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude="[*.Tests]*,[*.Test]*,[*Tests]*,[*Test]*,[*.Specs]*,[*.Testing]*" \
    DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/obj/**/*.cs,**/*.g.cs,**/*.g.i.cs,**/Protos/*.cs,**/Components/Pages/*.razor" \
    DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.SkipAutoProps=true
done

echo
echo "Coverage files:"
coverage_files=()
while IFS= read -r coverage_file; do
  coverage_files+=("$coverage_file")
  echo "$coverage_file"
done < <(find "$results_dir/raw" -name 'coverage.cobertura.xml' -print | sort)

if [[ "${#coverage_files[@]}" -eq 0 ]]; then
  echo "No coverage.cobertura.xml files were generated." >&2
  exit 1
fi

echo
if ! command -v python3 >/dev/null 2>&1; then
  if [[ -z "$line_threshold" && -z "$branch_threshold" && -z "$critical_line_threshold" && -z "$crap_threshold" ]]; then
    echo "python3 not found; skipping coverage summary and optional gates."
    exit 0
  fi

  echo "python3 is required to enforce coverage thresholds." >&2
  exit 1
fi

python3 - "$line_threshold" "$branch_threshold" "$critical_line_threshold" "$crap_threshold" "${coverage_files[@]}" <<'PY'
import sys
import xml.etree.ElementTree as ET

line_threshold_arg, branch_threshold_arg, critical_line_threshold_arg, crap_threshold_arg = sys.argv[1:5]
coverage_paths = sys.argv[5:]

critical_prefixes = (
    "src/RunnerRunner.Server/Services/",
    "src/RunnerRunner.Server/Grains/",
    "RunnerRunner.Server/Services/",
    "RunnerRunner.Server/Grains/",
)

def parse_percent(value, name):
    if not value:
        return None
    try:
        parsed = float(value)
    except ValueError:
        print(f"{name} must be numeric.", file=sys.stderr)
        sys.exit(2)
    if 0 <= parsed <= 1:
        parsed *= 100
    if parsed < 0 or parsed > 100:
        print(f"{name} must be between 0 and 100.", file=sys.stderr)
        sys.exit(2)
    return parsed

def parse_score(value, name):
    if not value:
        return None
    try:
        parsed = float(value)
    except ValueError:
        print(f"{name} must be numeric.", file=sys.stderr)
        sys.exit(2)
    if parsed < 0:
        print(f"{name} must be non-negative.", file=sys.stderr)
        sys.exit(2)
    return parsed

def percent(covered, valid):
    return 100.0 * covered / valid if valid else 0.0

def attr_int(element, name):
    value = element.get(name)
    return int(float(value)) if value not in (None, "") else None

def normalized(path):
    return path.replace("\\", "/")

def is_critical(filename):
    path = normalized(filename)
    return any(prefix in path for prefix in critical_prefixes)

line_threshold = parse_percent(line_threshold_arg, "line threshold")
branch_threshold = parse_percent(branch_threshold_arg, "branch threshold")
critical_line_threshold = parse_percent(critical_line_threshold_arg, "critical line threshold")
crap_threshold = parse_score(crap_threshold_arg, "CRAP threshold")

lines_covered = 0
lines_valid = 0
branches_covered = 0
branches_valid = 0
critical_lines_covered = 0
critical_lines_valid = 0
critical_methods = []

for coverage_path in coverage_paths:
    root = ET.parse(coverage_path).getroot()

    root_lines_covered = attr_int(root, "lines-covered")
    root_lines_valid = attr_int(root, "lines-valid")
    root_branches_covered = attr_int(root, "branches-covered")
    root_branches_valid = attr_int(root, "branches-valid")

    if root_lines_covered is not None and root_lines_valid is not None:
        lines_covered += root_lines_covered
        lines_valid += root_lines_valid
    else:
        for line in root.findall(".//line"):
            lines_valid += 1
            if int(line.get("hits", "0")) > 0:
                lines_covered += 1

    if root_branches_covered is not None and root_branches_valid is not None:
        branches_covered += root_branches_covered
        branches_valid += root_branches_valid

    for cls in root.findall(".//class"):
        filename = normalized(cls.get("filename", ""))
        class_is_critical = is_critical(filename)
        class_lines = cls.findall("./lines/line")

        if class_is_critical:
            critical_lines_valid += len(class_lines)
            critical_lines_covered += sum(1 for line in class_lines if int(line.get("hits", "0")) > 0)

            for method in cls.findall("./methods/method"):
                try:
                    complexity = float(method.get("complexity", "0") or "0")
                    coverage = float(method.get("line-rate", "0") or "0")
                except ValueError:
                    continue

                if complexity <= 0:
                    continue

                crap = (complexity * complexity * ((1 - coverage) ** 3)) + complexity
                critical_methods.append({
                    "crap": crap,
                    "complexity": complexity,
                    "coverage": coverage,
                    "name": method.get("name", "<unknown>"),
                    "file": filename,
                })

line_pct = percent(lines_covered, lines_valid)
branch_pct = percent(branches_covered, branches_valid)
critical_line_pct = percent(critical_lines_covered, critical_lines_valid)
critical_methods.sort(key=lambda item: item["crap"], reverse=True)

print("Coverage summary:")
print(f"  Lines:    {lines_covered}/{lines_valid} ({line_pct:.2f}%)")
if branches_valid:
    print(f"  Branches: {branches_covered}/{branches_valid} ({branch_pct:.2f}%)")
else:
    print("  Branches: no branch data")
if critical_lines_valid:
    print(f"  Critical Server Services/Grains lines: {critical_lines_covered}/{critical_lines_valid} ({critical_line_pct:.2f}%)")
else:
    print("  Critical Server Services/Grains lines: no matched files")

if critical_methods:
    print("  Top critical CRAP scores:")
    for method in critical_methods[:5]:
        print(
            f"    {method['crap']:.1f}  {method['name']} "
            f"(complexity {method['complexity']:.0f}, line {method['coverage'] * 100:.0f}%, {method['file']})"
        )

print("  Exclusions: obj, generated *.g.cs/*.g.i.cs, Protos, Components/Pages/*.razor")

failed = False
if line_threshold is None:
    print("Line coverage gate: not set (use --line-threshold).")
elif line_pct + 1e-9 < line_threshold:
    print(f"Line coverage gate failed: {line_pct:.2f}% < {line_threshold:.2f}%.", file=sys.stderr)
    failed = True
else:
    print(f"Line coverage gate passed: {line_pct:.2f}% >= {line_threshold:.2f}%.")

if branch_threshold is None:
    print("Branch coverage gate: not set (use --branch-threshold).")
elif branches_valid == 0:
    print("Branch coverage gate failed: no branch data.", file=sys.stderr)
    failed = True
elif branch_pct + 1e-9 < branch_threshold:
    print(f"Branch coverage gate failed: {branch_pct:.2f}% < {branch_threshold:.2f}%.", file=sys.stderr)
    failed = True
else:
    print(f"Branch coverage gate passed: {branch_pct:.2f}% >= {branch_threshold:.2f}%.")

if critical_line_threshold is None:
    print("Critical line coverage gate: not set (use --critical-line-threshold).")
elif critical_lines_valid == 0:
    print("Critical line coverage gate failed: no critical Server Services/Grains coverage data.", file=sys.stderr)
    failed = True
elif critical_line_pct + 1e-9 < critical_line_threshold:
    print(f"Critical line coverage gate failed: {critical_line_pct:.2f}% < {critical_line_threshold:.2f}%.", file=sys.stderr)
    failed = True
else:
    print(f"Critical line coverage gate passed: {critical_line_pct:.2f}% >= {critical_line_threshold:.2f}%.")

if crap_threshold is None:
    print("Critical CRAP gate: not set (use --crap-threshold).")
else:
    over_threshold = [method for method in critical_methods if method["crap"] > crap_threshold]
    if over_threshold:
        print(
            f"Critical CRAP gate failed: {len(over_threshold)} method(s) above {crap_threshold:.1f}.",
            file=sys.stderr,
        )
        failed = True
    else:
        print(f"Critical CRAP gate passed: no methods above {crap_threshold:.1f}.")

sys.exit(1 if failed else 0)
PY
