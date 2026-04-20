@echo off
setlocal enabledelayedexpansion

set "STS2_DIR=C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"

set "NUM_PLAYERS=%~1"
if "%NUM_PLAYERS%"=="" set "NUM_PLAYERS=2"

if %NUM_PLAYERS% LSS 2 (
    echo Need at least 2 players. Usage: test-mp.bat [num_players]
    exit /b 1
)

pushd "%STS2_DIR%" || (
    echo Could not cd to "%STS2_DIR%". Edit STS2_DIR in this script.
    exit /b 1
)

if not exist "steam_appid.txt" (
    echo Creating steam_appid.txt so the game can launch without Steam...
    >"steam_appid.txt" echo 2868840
)

echo Launching host...
start "STS2 host" "SlayTheSpire2.exe" -fastmp host_standard

set /A NUM_CLIENTS=%NUM_PLAYERS%-2
for /L %%i in (0,1,!NUM_CLIENTS!) do (
    set /A CID=1000+%%i
    echo Launching client with clientId !CID!...
    start "STS2 client !CID!" "SlayTheSpire2.exe" -fastmp join -clientId !CID!
)

popd
echo Launched %NUM_PLAYERS% instances. Close each window manually when done.
