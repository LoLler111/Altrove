@echo off
rem Prova dello schermo virtuale: scrive tutto in risultato-estendi.txt, accanto a questo file.
chcp 65001 >nul
echo Installo il driver dello schermo virtuale: Windows chiedera' il permesso, rispondi Si.
"%~dp0altrove-cli.exe" vdd-install > "%~dp0risultato-estendi.txt" 2>&1
echo.
echo Ora compare un nuovo schermo a destra degli altri, per 10 secondi:
echo trascinaci sopra una finestra e muovila.
(
  echo.
  echo === extend ===
  "%~dp0altrove-cli.exe" extend --seconds 10 --display 1920x1200 --out "%~dp0estendi-test.h264"
  echo.
  echo === vdd ===
  "%~dp0altrove-cli.exe" vdd
) >> "%~dp0risultato-estendi.txt" 2>&1
type "%~dp0risultato-estendi.txt"
echo.
echo Fatto. Manda risultato-estendi.txt ed estendi-test.bmp.
pause
