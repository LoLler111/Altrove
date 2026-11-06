@echo off
rem Prova dell'encoder: scrive tutto in risultato.txt, accanto a questo file.
cd /d "%~dp0"
chcp 65001 >nul
echo Muovi una finestra sullo schermo per i prossimi 5 secondi...
(
  echo === list ===
  "%~dp0altrove-cli.exe" list
  echo.
  echo === record ===
  "%~dp0altrove-cli.exe" record --seconds 5 --out "%~dp0altrove-test.h264"
) > "%~dp0risultato.txt" 2>&1
type "%~dp0risultato.txt"
echo.
echo Fatto. Manda risultato.txt e altrove-test.bmp.
pause
