#!/usr/bin/env python3
"""Run all portable Discover regression harnesses (not native UI tests)."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
SUITES = [
    ('qa/check_manage_mod_actions.py', False),
    ('Tests/DiscoverArtworkRequestRegression_test.py', False),
    ('Tests/GUIRegression/catalogue_pane_regression.py', True),
    ('Tests/GUI/test_modern_settings_contract.py', False),
    ('Tests/GUI/test_modern_settings_runtime.py', False),
    ('qa/check_mod_card_keyboard.py', True),
    ('qa/check_pane_timing.py', False),
    ('qa/check_changelog_requests.py', False),
    ('qa/check_changelog_service.py', False),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default=os.environ.get('DOTNET') or shutil.which('dotnet'))
    args = parser.parse_args()
    if not args.dotnet:
        parser.error('A .NET 10 SDK is required; provide --dotnet or DOTNET')
    dotnet = shutil.which(args.dotnet)
    if not dotnet:
        parser.error('The specified dotnet executable was not found')
    env = dict(os.environ, DOTNET=dotnet)
    # A baseline diagnostic must not accidentally replace the working-tree run.
    env.pop('CKAN_SETTINGS_BASELINE', None)
    failures = []
    for path, accepts_argument in SUITES:
        print(f'\n=== {path} ===', flush=True)
        command = [sys.executable, str(ROOT / path)]
        if accepts_argument:
            command += ['--dotnet', dotnet]
        try:
            result = subprocess.run(command, cwd=ROOT, env=env, timeout=180)
            if result.returncode:
                failures.append(path)
        except subprocess.TimeoutExpired:
            print('FAILED: suite timed out', flush=True)
            failures.append(path)
    print(f'\nPortable suites: {len(SUITES) - len(failures)} passed, {len(failures)} failed.')
    for path in failures:
        print('FAILED:', path)
    return 1 if failures else 0


if __name__ == '__main__':
    raise SystemExit(main())
