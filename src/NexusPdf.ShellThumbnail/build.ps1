# Сборка обработчика эскизов PDF для Проводника.
#
# Отдельным скриптом, а не проектом в общем решении: зависимость одна (Windows
# SDK), исходник один, а .NET-часть продукта от него не зависит — тянуть в
# .slnx проект C++ ради одного файла незачем.
#
# Окружение компилятора забирается у vcvars64.bat ОДИН раз и переносится в эту
# сессию, после чего cl.exe вызывается напрямую. Прежний вариант собирал
# команду во временный .bat и молча не работал: в PowerShell запятая связывает
# сильнее плюса, поэтому строка `'call "' + $vcvars + '" >nul'` внутри списка
# распадалась на три элемента массива и в файл уезжала строкой `call "`.
# Компилятор при этом не запускался ни разу, а скрипт рапортовал об успехе.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Не найден vswhere.exe — Visual Studio не установлена." }

$vs = @(& $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)[0]
if (-not $vs) { throw "Не найдена Visual Studio с инструментами C++ (workload 'Разработка классических приложений на C++')." }

$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "Не найден $vcvars" }

$root = $PSScriptRoot
$out = Join-Path $root "bin"
New-Item -ItemType Directory -Force $out | Out-Null

# Старый результат убираем ДО сборки. Иначе провалившаяся компиляция оставляет
# на месте вчерашний файл, проверка «библиотека есть» проходит, и скрипт
# рапортует об успехе, которого не было.
$dll = Join-Path $out "NexusPdfThumbnail.dll"
Remove-Item $dll -Force -ErrorAction SilentlyContinue

# Переменные среды компилятора: единственный способ получить их от vcvars —
# запустить его и распечатать окружение. Собственный вывод vcvars уходит в
# никуда, разбираем только строки после маркера.
$marker = "===ENV==="
$envBat = Join-Path ([System.IO.Path]::GetTempPath()) "nexus-thumb-env.bat"
# Каждая строка — отдельный элемент массива и целиком в скобках: без них
# запятая перехватывает конкатенацию и строка распадается на куски.
# Оба потока vcvars уходят в никуда намеренно. На машинах, где Visual Studio
# стоит не в Program Files, он ругается в поток ошибок на собственный vswhere,
# успешно доделывая работу; а любая строка в потоке ошибок при
# ErrorActionPreference = Stop у вызывающего роняет всю сборку продукта.
Set-Content -Path $envBat -Encoding ASCII -Value @(
    '@echo off',
    ('call "' + $vcvars + '" >nul 2>&1'),
    ('echo ' + $marker),
    'set'
)
$dump = & cmd.exe /c $envBat
Remove-Item $envBat -Force -ErrorAction SilentlyContinue
$seen = $false
foreach ($line in $dump) {
    if (-not $seen) { if ($line -eq $marker) { $seen = $true }; continue }
    $eq = $line.IndexOf('=')
    if ($eq -gt 0) {
        $name = $line.Substring(0, $eq)
        $value = $line.Substring($eq + 1)
        Set-Item -Path "Env:$name" -Value $value -ErrorAction SilentlyContinue
    }
}
if (-not $seen) { throw "Не удалось получить окружение компилятора из $vcvars" }

$cl = Get-Command cl.exe -ErrorAction SilentlyContinue
if (-not $cl) { throw "cl.exe не найден даже после vcvars64.bat — установка Visual Studio неполная." }

# Отладочная конфигурация отличается только оптимизацией: обработчик грузится
# в чужой процесс, и лишних зависимостей у него быть не должно ни в каком виде,
# поэтому /MT остаётся всегда.
$optimize = if ($Configuration -eq "Debug") { "/Od" } else { "/O2" }

Push-Location $root
try {
    $args = @(
        "/nologo", $optimize, "/MT", "/EHsc", "/W4", "/DUNICODE", "/D_UNICODE",
        "/Fobin\", "NexusPdfThumbnail.cpp",
        "/link", "/DLL", "/DEF:NexusPdfThumbnail.def", "/OUT:bin\NexusPdfThumbnail.dll",
        "ole32.lib", "shlwapi.lib", "user32.lib", "gdi32.lib", "advapi32.lib", "shell32.lib"
    )
    & $cl.Source @args
    $code = $LASTEXITCODE
}
finally { Pop-Location }

if ($code -ne 0) { throw "Сборка обработчика эскизов не удалась (код $code)." }
if (-not (Test-Path $dll)) { throw "Библиотека не создана: $dll" }

# Библиотека обязана быть свежее исходника: одного факта её существования мало.
$srcTime = (Get-Item (Join-Path $root "NexusPdfThumbnail.cpp")).LastWriteTime
$dllTime = (Get-Item $dll).LastWriteTime
if ($dllTime -lt $srcTime) {
    throw "Библиотека старше исходника ($dllTime против $srcTime) — компиляция не выполнилась."
}
"{0}" -f $dll
"{0:N0} байт, собрано {1:HH:mm:ss}" -f (Get-Item $dll).Length, $dllTime
