# Prepares an isolated CAB data folder and a small sample project for the demo video.
param([ValidateSet('ja', 'en')][string]$Lang = 'ja', [ValidateSet('light', 'dark')][string]$Theme = 'light', [switch]$AutoSwitch, [switch]$SeedActivity)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
. (Join-Path $PSScriptRoot 'demo-env.ps1')
. (Join-Path $PSScriptRoot 'demo-cleanup-guard.ps1')
if (-not $AgyExe) { throw 'Antigravity CLI (agy) was not found. Set CAB_DEMO_AGY.' }
$data = Resolve-CabDemoPath $CabData
$ws = Resolve-CabDemoPath (Join-Path $SampleRoot 'weather-cli')
$todo = Resolve-CabDemoPath (Join-Path $SampleRoot 'todo-cli')
$data = Reset-CabDemoDirectory $data $DemoData
$legacySamples = Join-Path $DemoData 'samples'
$ws = Reset-CabDemoDirectory $ws $legacySamples
$todo = Reset-CabDemoDirectory $todo $legacySamples
$mm = Get-Content $MattermostState -Raw | ConvertFrom-Json
$token = $mm.'cab-bot'.token
$protected = 'dpapi:' + [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
    [Text.Encoding]::UTF8.GetBytes($token), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
function J($name, $value) { [IO.File]::WriteAllText((Join-Path $data $name), ($value | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false)) }
J 'settings.json' @{
    SchemaVersion = 1; WorkspaceRoot = $ws; TrustedWorkspaceRoots = "$ws`r`n$todo"
    AgentProvider = 'antigravity'; AgentMode = 'exec'; AgentExecutable = $AgyExe
    AgentArguments = '--output-format stream-json'; AgentPermissionMode = 'workspace-write'; RequireApproval = $true
    MattermostServerUrl = $mm.serverUrl; MattermostChannelId = $mm.channelId; MattermostToken = $protected
    MattermostAllowedUserIds = $mm.alice.id; MattermostBypassProxy = $true
    ChatApprovalMode = $(if ($AutoSwitch) { 'auto' } else { 'first' }); AutoApproveSwitchCommands = [bool]$AutoSwitch; ChatResultMode = 'auto'; ChatFollowUpMode = 'queue'; AutoConnectProvider = 'none'; FileAttachmentApprovalMode = 'always'
}
J 'onboarding.json' @{ schemaVersion = 3; step = 4; status = 'completed' }
# Update the policy version when the app's consent policy changes, or the consent screen appears on camera.
J 'consent.json' @{ PolicyVersion = '2026-10-20.2'; AcceptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
J 'language.json' @{ Language = $Lang }
J 'appearance.json' @{ Theme = $Theme }
J 'tips.json' @{ Seen = @('chat-connect') }
# CLIs a conversation can switch to with "!cab agent <name>" (same shape as a verified registration).
$checkedAt = [DateTimeOffset]::UtcNow.ToString('o')
function Version($exe) { (& $exe --version 2>&1 | Select-Object -First 1).ToString().Trim() }
$profiles = @(@{ Provider = 'antigravity'; Executable = $AgyExe; Arguments = '--output-format stream-json'
    Mode = 'exec'; Model = $null; ReasoningEffort = $null; PermissionMode = 'workspace-write'; AllowNetwork = $false
    CliVersion = (Version $AgyExe); CheckedAt = $checkedAt })
if ($ClaudeExe) {
    $profiles += @{ Provider = 'claude'; Executable = $ClaudeExe; Arguments = ''
        Mode = 'exec'; Model = $null; ReasoningEffort = $null; PermissionMode = 'workspace-write'; AllowNetwork = $false
        CliVersion = (Version $ClaudeExe); CheckedAt = $checkedAt }
}
J 'agent-connections.json' @{ SchemaVersion = 1; Profiles = $profiles }

# A tiny sample project the agent can read and explain.
function F($path, $text) { $full = Join-Path $ws $path; New-Item -ItemType Directory (Split-Path $full) -Force | Out-Null; [IO.File]::WriteAllText($full, $text, [Text.UTF8Encoding]::new($false)) }
F 'README.md' @'
# weather-cli

都市名を渡すと、今日の天気と気温をターミナルに表示する小さな CLI です。

## 使い方

```
python weather.py Tokyo
```

## テスト

```
python -m unittest
```
'@
F 'weather.py' @'
import sys

FORECASTS = {"tokyo": ("晴れ", 22), "osaka": ("くもり", 20), "sapporo": ("雨", 12)}


def describe(city: str) -> str:
    weather, temperature = FORECASTS.get(city.lower(), ("不明", None))
    if temperature is None:
        return f"{city}: 予報が見つかりません"
    return f"{city}: {weather} {temperature}°C"


if __name__ == "__main__":
    print(describe(sys.argv[1] if len(sys.argv) > 1 else "Tokyo"))
'@
F 'test_weather.py' @'
import unittest

from weather import describe


class DescribeTests(unittest.TestCase):
    def test_known_city(self):
        self.assertEqual(describe("Tokyo"), "Tokyo: 晴れ 22°C")

    def test_unknown_city(self):
        self.assertIn("見つかりません", describe("Paris"))


if __name__ == "__main__":
    unittest.main()
'@
if ($Lang -eq 'en') {
F 'README.md' @'
# weather-cli

A tiny CLI that prints today's weather and temperature for a city.

## Usage

```
python weather.py Tokyo
```

## Tests

```
python -m unittest
```
'@
F 'weather.py' @'
import sys

FORECASTS = {"tokyo": ("Sunny", 22), "osaka": ("Cloudy", 20), "sapporo": ("Rain", 12)}


def describe(city: str) -> str:
    weather, temperature = FORECASTS.get(city.lower(), ("Unknown", None))
    if temperature is None:
        return f"{city}: no forecast found"
    return f"{city}: {weather} {temperature}°C"


if __name__ == "__main__":
    print(describe(sys.argv[1] if len(sys.argv) > 1 else "Tokyo"))
'@
F 'test_weather.py' @'
import unittest

from weather import describe


class DescribeTests(unittest.TestCase):
    def test_known_city(self):
        self.assertEqual(describe("Tokyo"), "Tokyo: Sunny 22°C")

    def test_unknown_city(self):
        self.assertIn("no forecast", describe("Paris"))


if __name__ == "__main__":
    unittest.main()
'@
}
# A second project for the folder-switch demo.
function T($path, $text) { $full = Join-Path $todo $path; [IO.File]::WriteAllText($full, $text, [Text.UTF8Encoding]::new($false)) }
T 'README.md' @'
# todo-cli

タスクを追加・一覧・完了できる、ファイル保存型の小さな ToDo CLI です。

## 使い方

```
python todo.py add "牛乳を買う"
python todo.py list
python todo.py done 1
```
'@
T 'todo.py' @'
import json
import sys
from pathlib import Path

STORE = Path(__file__).with_name("todos.json")


def load():
    return json.loads(STORE.read_text(encoding="utf-8")) if STORE.exists() else []


def save(todos):
    STORE.write_text(json.dumps(todos, ensure_ascii=False, indent=2), encoding="utf-8")


def main(args):
    todos = load()
    if args[:1] == ["add"]:
        todos.append({"title": " ".join(args[1:]), "done": False})
    elif args[:1] == ["done"]:
        todos[int(args[1]) - 1]["done"] = True
    for number, todo in enumerate(todos, 1):
        print(f"{number}. [{'x' if todo['done'] else ' '}] {todo['title']}")
    save(todos)


if __name__ == "__main__":
    main(sys.argv[1:])
'@
if ($SeedActivity) {
    # Sample conversations and a month of generated usage for the History and usage demo (not real measurements).
    dotnet run (Join-Path $PSScriptRoot 'SeedActivity.cs') -- $data $MattermostState $Lang $ws $todo
    if ($LASTEXITCODE -ne 0) { throw 'Seeding sample activity failed.' }
}
"cab demo data ready: $data ($Lang)"
