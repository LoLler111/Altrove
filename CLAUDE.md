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
- Prima scelta sempre l'encoder hardware della GPU che pilota lo schermo; `ParameterSetCache` garantisce SPS/PPS davanti a ogni IDR.

## Test
- `dotnet test tests/Altrove.Core.Tests`; Android: `./gradlew testDebugUnitTest` (JDK di Android Studio).
- `tools/Altrove.Cli` prova cattura ed encoder senza interfaccia: `record` ridecodifica con il decoder di Windows e salva un fotogramma.
- Ricevitore Android nell'emulatore: `adb forward tcp:47800 tcp:47800`, poi `altrove-cli send localhost:47800`.
  La ricerca mDNS dall'emulatore non si vede (rete NAT): per provarla usare `altrove-cli announce` + `discover`.
- La VM di sviluppo non ha GPU (Microsoft Basic Render Driver): lì gira solo il percorso CPU + encoder software.
  Il percorso hardware (GPU, AMF della RX 6750 XT) va provato sul PC fisso.
