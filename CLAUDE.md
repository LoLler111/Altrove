# Altrove – note per lo sviluppo

Testi per l'utente in italiano, commenti e nomi nel codice in inglese (come in SoundBoardKing).

## Architettura
- Il protocollo è la fonte di verità: `docs/PROTOCOLLO.md`. Ogni modifica va fatta in tutti e due i lati
  (`src/Altrove.Core/Protocol` e `android/.../protocol/Protocol.kt`) e nei rispettivi test, che usano gli stessi messaggi.
- Fase 1: il ricevitore Android è il server WebSocket (porta 47800, annunciato `_altrove._tcp`), il PC si collega.
  I ricevitori web (fase 3) invertiranno i ruoli di rete, non i messaggi.
- Pipeline Windows: `ScreenCapture` (WGC, frame BGRA) → `INv12Converter` (video processor GPU, oppure CPU) →
  `H264Encoder` (MFT hardware asincrono con texture D3D11, oppure MFT software sincrono da memoria) →
  `SenderSession` (coda con scarto fino al prossimo IDR quando la rete rallenta).
- Estendi: `VirtualDisplay/` comanda il Virtual Display Driver (`third_party/`, file firmati: non modificarli,
  sono binari in git). Il driver non scende sotto 1 monitor: il monitor non si elimina, si stacca dal desktop
  (`DisplaySettings.Detach`). `RELOAD_DRIVER` rilegge `vdd_settings.xml` al volo; la pipe accetta comandi ma non risponde.
- Prima scelta sempre l'encoder hardware della GPU che pilota lo schermo; `ParameterSetCache` garantisce SPS/PPS davanti a ogni IDR.
- L'encoder software scrive nel campione che gli passiamo: `H264Encoder` ne riusa uno solo. Uno nuovo per ogni
  uscita non veniva liberato e mandava il processo fuori memoria in ~30 s (`altrove-cli stress encode` lo verifica).
- Funzioni facoltative del protocollo (`features` nell'hello): `touch` (`Capture/Input/TouchInjector`, device di
  tocco sintetico di Windows, ripete da sé la posizione di un dito fermo) e `configure` (il ricevitore cambia le
  impostazioni: `MainViewModel.ConfigureAsync` → `StartStreamAsync` → `SenderSession.SwitchSourceAsync`, stessa connessione).
- `ScreenVideoSource`: il fotogramma in conversione e lo smontaggio sono esclusivi (`_frameLock`).
- Risoluzioni: `Core/Video/ResolutionPlanner` (Duplica: lo schermo rimpicciolito, mai ingrandito; Estendi: forma del
  ricevitore, anche più grande). Ogni scelta ha come chiave l'altezza risultante. Il ricevitore riporta in `maxWidth`/`maxHeight`
  il limite del decoder, non del proprio schermo. Trackpad: gesti in `receiver/TrackpadGestures.kt`, puntatore in
  `Capture/Input/MouseInjector`. Scala di Estendi: `VirtualDisplay/DisplayScale` (richieste DPI non documentate -3/-4).
- Audio: `Capture/Audio/LoopbackAudio` (WASAPI loopback con NAudio, Opus a 10 ms con Concentus, oppure PCM) →
  `SenderSession.SendAudio` (coda propria, prima del video) → pacchetti binari tipo 2 → `receiver/AudioPlayer.kt`
  (decoder Opus di Android, AudioTrack a bassa latenza, scarta oltre 150 ms). `altrove-cli audio` lo prova senza tablet.

## Rilasci e aggiornamenti
- Repository pubblico `LoLler111/Altrove`; un tag `vX.Y.Z` uguale a `version.txt` fa partire `.github/workflows/release.yml`
  (`publish.ps1` + `vpk upload github`, APK aggiunto prima di togliere la bozza). Schema copiato da SoundBoardKing.
- Windows: Velopack (`Program.Main` → `VelopackApp.Build().Run()`, `UpdateService`). Si aggiorna solo se installato
  con Setup.exe o dallo zip Portable. Prova locale: `ALTROVE_UPDATE_SOURCE=<cartella di artifacts\releases>`.
- Android: pacchetto `com.altrove.app` (definitivo). `update/UpdateController` legge `releases/latest` di GitHub,
  scarica l'APK e apre l'installer. Chiave in `~/.altrove` (o variabili `ALTROVE_KEYSTORE*` in CI): senza, l'APK di
  release esce firmato debug e non aggiorna quello pubblicato. Prova locale: build debug con
  `-PupdateUrl=http://10.0.2.2:8765/latest.json` (HTTP in chiaro permesso solo in `src/debug`).

## Test
- `dotnet test tests/Altrove.Core.Tests`; Android: `./gradlew testDebugUnitTest` (JDK di Android Studio).
- `tools/Altrove.Cli` prova cattura ed encoder senza interfaccia: `record` ridecodifica con il decoder di Windows e salva un fotogramma.
- Ricevitore Android nell'emulatore: `adb forward tcp:47800 tcp:47800`, poi `altrove-cli send localhost:47800`.
  La ricerca mDNS dall'emulatore non si vede (rete NAT): per provarla usare `altrove-cli announce` + `discover`.
- La VM di sviluppo non ha GPU (Microsoft Basic Render Driver): lì gira solo il percorso CPU + encoder software.
  Il percorso hardware (GPU, AMF della RX 6750 XT) va provato sul PC fisso.
- La VM si usa in sessione remota: il driver virtuale è installato lì, ma il suo monitor resta nella sessione
  console e non si vede. Attacco/stacco e cattura del monitor virtuale si provano solo sul fisso.
- Prova del pannello e del tocco nell'emulatore: girarlo in orizzontale, avviare `Altrove.exe` con Start-Process
  (un processo lanciato dentro un comando Bash muore con il comando) e guidarlo con UI Automation; sull'emulatore
  `adb shell uiautomator dump` trova i pulsanti per testo. Gli errori dell'app finiscono in `%LOCALAPPDATA%\Altrove\altrove.log`.
- Zip di prova per il fisso: `dotnet publish` autonomo (win-x64) di App e Cli in `artifacts/Altrove-prova`,
  più `tools/prova/*` e l'APK.
