<#
Builds src/Arkive.App/Assets/Tweaks/escape-menu.jpg, the Tweaks tab's picture of the escape menu with the Arkive Tweaks
button marked, from the game's own interface movie, art and fonts (placeholder character data; see scene.py).

  -SystemMenuPackage  the game's efui_systemmenu package (its scrambled .upk file; use clean game files)
  -Movie              that package's exitmenu movie, inflated (as Arkive's interface movie reader writes it)
  -Fonts              a folder with ExuberanceF-Regular.ttf and MonarchaF-Regular.ttf (font.lpk, /Binaries/Fonts/English)
  -Work               an empty scratch folder

Needs Python 3 with Pillow, and FFDec, a Java runtime and UModel (the tools Build-Tweaks.ps1 and Arkive use).
#>
param(
    [Parameter(Mandatory)] [string]$SystemMenuPackage,
    [Parameter(Mandatory)] [string]$Movie,
    [Parameter(Mandatory)] [string]$Fonts,
    [Parameter(Mandatory)] [string]$Work,
    [string]$Tools = "E:\ArkiveTools",
    [string]$UModel = "E:\Arkive\tools\external\umodel_lostark_v7.exe"
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$java = Get-ChildItem "$Tools\jre" -Recurse -Filter java.exe | Select-Object -First 1
$ffdec = "$Tools\ffdec\ffdec-cli.jar"
$movies = Join-Path $Work "EFUI_SystemMenu"
New-Item -ItemType Directory -Force $movies | Out-Null
Copy-Item "$Fonts\ExuberanceF-Regular.ttf", "$Fonts\MonarchaF-Regular.ttf" $Work

# The package's textures (UModel writes PNGs), then the TGA files the movie names.
& $UModel -game=lostark "-path=$(Split-Path $SystemMenuPackage)" "-out=$Work\textures" -export -png $SystemMenuPackage | Out-Null
Copy-Item $Movie "$movies\exitMenu.gfx"
& $java.FullName -jar $ffdec -dumpSWF "$movies\exitMenu.gfx" > "$Work\dump.txt"
& $java.FullName -jar $ffdec -swf2xml "$movies\exitMenu.gfx" "$Work\exitmenu.xml" | Out-Null
python "$here\totga.py" "$Work\textures" "$Work\dump.txt" $movies

# The filled menu: placements for FFDec to draw, then the text in the game's fonts and the mark.
python "$here\scene.py" $Work
python "$here\build.py" "$Work\exitmenu.xml" $movies "$Work\built.xml" "$Work\layout.json"
& $java.FullName -jar $ffdec -xml2swf "$Work\built.xml" "$movies\built.gfx" | Out-Null
& $java.FullName -jar $ffdec -format frame:png -select 1 -export frame "$Work\frame" "$movies\built.gfx" | Out-Null
python "$here\overlay.py" $Work "$Work\frame\1.png" (Join-Path $here "..\..\..\src\Arkive.App\Assets\Tweaks\escape-menu.jpg")
