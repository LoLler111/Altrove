# Altrove

*Il tuo schermo, altrove.*

Altrove proietta lo schermo del PC Windows su tablet, telefoni, TV e chiavette TV della rete di casa.
Il PC cattura lo schermo, lo comprime in H.264 con l'encoder della scheda video e lo manda sulla rete
locale a un'app ricevitore. Non serve Miracast e funziona anche con un PC collegato via cavo.

## Stato

| Pezzo | Stato |
|---|---|
| Protocollo comune ([docs/PROTOCOLLO.md](docs/PROTOCOLLO.md)) | fatto |
| App Windows: duplica uno schermo verso un ricevitore | fatto |
| App Android: ricevitore (tablet, telefono, Fire TV con Fire OS) | fatto |
| Schermo virtuale ed estensione (fase 2) | provato sul PC fisso; manca la prova con il tablet |
| Dal tablet: tocco verso il PC e pannello delle impostazioni (fase 5) | provato nell'emulatore; manca la prova con il tablet |
| Dal tablet: audio del PC, microfono del tablet (fase 5) | da fare |
| Ricevitore web per Chromecast e TV LG webOS (fase 3) | da fare |
| App Android come trasmettitore (fase 4) | da fare |

## Struttura

| Cartella | Cosa contiene |
|---|---|
| `src/Altrove.Core` | Protocollo, ricerca mDNS dei ricevitori, sessione di trasmissione. Nessuna dipendenza da Windows. |
| `src/Altrove.Capture` | Cattura (Windows.Graphics.Capture), conversione NV12 (GPU o CPU), encoder H.264 Media Foundation (hardware o software). |
| `src/Altrove.App` | L'app Windows (WPF). |
| `tools/Altrove.Cli` | Banco di prova da riga di comando, vedi sotto. |
| `tests/Altrove.Core.Tests` | Test di protocollo, mDNS e sessione. |
| `android/` | L'APK Android (Kotlin + Compose): per ora solo la modalità ricevitore. |

## Installare

Dalla pagina [Releases](https://github.com/LoLler111/Altrove/releases/latest):

- **Windows**: `Altrove-win-Setup.exe` (oppure `Altrove-win-Portable.zip` da scompattare dove vuoi). Windows può
  avvisare che l'editore è sconosciuto: «Ulteriori informazioni» → «Esegui comunque».
- **Android** (tablet, telefono, Fire TV): `Altrove-android.apk`. Serve consentire l'installazione da origini sconosciute.

Poi le app **si aggiornano da sole** dalle stesse release:

- Windows controlla all'avvio, scarica in silenzio e installa alla chiusura; il pulsante «Aggiorna alla …» in alto
  lo fa subito (Altrove si chiude e si riapre).
- Android, nella schermata d'attesa, mostra «È disponibile Altrove …»: «Aggiorna» scarica l'APK e Android chiede di
  confermare l'installazione (la prima volta anche il permesso di installare app).

## Pubblicare una versione

1. Cambia `version.txt` (es. `0.3.0`), fai il commit e caricalo su GitHub.
2. Crea e carica il tag con lo stesso numero:

```bash
git tag v0.3.0 && git push origin v0.3.0
```

Il workflow `Release` esegue i test, costruisce installer, pacchetti di aggiornamento e APK firmato e pubblica la
release. In locale `./publish.ps1` produce gli stessi file in `artifacts/releases`.

L'APK va firmato sempre con la stessa chiave, altrimenti Android rifiuta l'aggiornamento: è in
`%USERPROFILE%\.altrove` (mai nel repository; conservane una copia) e su GitHub nei secret
`ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`.

## Compilare

Windows (serve l'SDK .NET 10):

```bash
dotnet build Altrove.slnx
```

```bash
dotnet test tests/Altrove.Core.Tests
```

Android (serve l'SDK Android; il JDK è quello di Android Studio):

```bash
cd android && JAVA_HOME="/c/Program Files/Android/Android Studio/jbr" ./gradlew testDebugUnitTest assembleDebug
```

L'APK di prova finisce in `android/app/build/outputs/apk/debug/app-debug.apk`.

## Provare

1. Installa l'APK sul tablet o sulla Fire TV e aprilo: mostra «Pronto a ricevere» e il suo indirizzo.
2. Avvia `Altrove.exe` sul PC: il ricevitore compare da solo nell'elenco. Se non compare, scrivi
   l'indirizzo mostrato dal ricevitore.
3. Scegli schermo e qualità, poi premi **Trasmetti**.

### Dal tablet

- **Tocco**: con «Usa il PC con il tocco dal ricevitore» attivo sul PC, un tocco sull'immagine è un clic,
  tenere premuto è il clic destro, trascinare sposta, due dita scorrono dove l'applicazione lo gestisce.
  Funziona in Duplica e in Estendi, anche su PC senza schermo touch. Windows non lascia toccare le finestre
  aperte come amministratore né le richieste di permesso (UAC).
- **Pannello**: la linguetta sul bordo sinistro, o il tasto Indietro, apre il pannello. Da lì si cambiano
  modalità, schermo, risoluzione, fluidità e qualità senza staccarsi: il PC riparte con le nuove impostazioni
  in un paio di secondi e se le ricorda. Il pannello mostra anche fps e tempi di decodifica e ha il pulsante per
  interrompere. Senza tocco (o con il tocco spento nel pannello) basta toccare lo schermo per aprirlo.

La prima volta Windows può chiedere di permettere ad Altrove l'accesso alla rete: va consentito per le reti private.

### Banco di prova (`altrove-cli`)

| Comando | A cosa serve |
|---|---|
| `altrove-cli list` | Schermi ed encoder H.264 disponibili |
| `altrove-cli record --seconds 5 --height 720` | Cattura e comprime per qualche secondo, salva il `.h264` e lo ridecodifica con il decoder di Windows per verificarlo; salva anche il primo fotogramma come `.bmp` |
| `altrove-cli discover` | Elenca i ricevitori annunciati in rete |
| `altrove-cli send 192.168.1.20` | Trasmette a un ricevitore e stampa le statistiche ogni secondo |
| `altrove-cli fake-receiver --port 47811` | Un ricevitore finto su localhost che conta cosa arriva |
| `altrove-cli announce --port 47811` | Fa annunciare a Windows un ricevitore di prova, per testare la ricerca |
| `altrove-cli analyze file.h264` | Conta le unità NAL per tipo e quanto del flusso è riempitivo; lo ridecodifica e salva il primo fotogramma come `.bmp` |
| `altrove-cli vdd-install` / `vdd-uninstall` | Installa o toglie il driver dello schermo virtuale (chiede i permessi di amministratore) |
| `altrove-cli extend --display 1920x1200` | Attacca lo schermo virtuale a quella misura, lo registra come `record` e lo stacca |
| `altrove-cli vdd [COMANDO]` | Stato del driver e dei display; manda un comando alla sua pipe (es. `RELOAD_DRIVER`) |
| `altrove-cli touch 0.5,0.5 [--seconds 2]` | Un tocco (o una pressione lunga) come lo manderebbe il ricevitore, a x,y da 0 a 1 sullo schermo `--monitor N` |
| `altrove-cli stress capture\|convert\|encode --seconds 60` | Un pezzo della catena da solo per un minuto, per cercare perdite e crash |

### Schermo virtuale (Estendi)

Usa il [Virtual Display Driver](third_party/VirtualDisplayDriver/README.md) (MIT, firmato da SignPath Foundation):
la prima volta che scegli **Estendi**, Altrove lo installa (Windows chiede il permesso). Durante la trasmissione
il driver espone un monitor della misura dello schermo del ricevitore, a destra degli altri; a fine trasmissione
Altrove lo stacca dal desktop. Le impostazioni del driver stanno in `C:\ProgramData\Altrove\VirtualDisplay`.
In una sessione Desktop remoto il monitor virtuale non si vede: va usato dal PC stesso.

Opzioni comuni: `--fps`, `--height`, `--kbps`, `--monitor N`, `--software` (forza l'encoder software).

## Limiti noti della fase 1

- Niente audio.
- Nessun abbinamento: qualunque PC della rete può trasmettere a un ricevitore aperto.
- I contenuti protetti (Netflix e simili) risultano neri: lo impone il DRM di Windows.
- Sui Fire TV con Vega OS (Fire TV Stick 4K Select, Fire TV Stick HD 2026) l'APK non si installa.
