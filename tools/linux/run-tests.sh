#!/usr/bin/env bash
# Runs inside the container of Test-Linux.ps1. Copies the files named in /list from the read-only mount /src to
# /work, builds Core, UI and AppTrace with -warnaserror like the CI's Linux job and runs dotnet test for each
# project given as an argument. Exit code 1 if anything failed.
set -uo pipefail

mkdir -p /work
tar -C /src --null -T /list -cf - | tar -C /work -xf - || { echo "== Copying the working tree failed"; exit 2; }
cd /work

failed=()
for project in src/FerretSharp.Core src/FerretSharp.UI src/FerretSharp.AppTrace; do
  echo "== dotnet build $project -warnaserror"
  dotnet build "$project" -warnaserror || failed+=("build $project")
done
for project in "$@"; do
  echo "== dotnet test $project"
  dotnet test "$project" || failed+=("test $project")
done

if [ ${#failed[@]} -gt 0 ]; then
  echo "== Failed under Linux: ${failed[*]}"
  exit 1
fi
echo "== Green under Linux"
