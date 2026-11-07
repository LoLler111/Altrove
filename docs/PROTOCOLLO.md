# Protocollo Altrove (v1)

Un **trasmettitore** (l'app Windows, più avanti anche l'app Android) manda lo schermo a un **ricevitore**
(l'APK Android su tablet, telefono o Fire TV; più avanti il ricevitore web per Chromecast e LG).

Il protocollo è lo stesso in tutti i casi; cambia solo chi apre la connessione:

| Ricevitore | Chi ascolta | Chi si collega |
|---|---|---|
| APK Android (fase 1) | il ricevitore, porta TCP **47800** | il trasmettitore |
| Ricevitore web (fase 3) | il trasmettitore (il browser non può ascoltare) | il ricevitore |

Una volta aperta la connessione WebSocket i messaggi sono identici: testo JSON per il controllo,
binario per il video.

## Scoperta

Il ricevitore si annuncia in mDNS/DNS-SD come servizio **`_altrove._tcp`** sulla sua porta.
Il record TXT contiene:

| Chiave | Esempio | Significato |
|---|---|---|
| `id` | `5b0c…` | identificativo stabile del dispositivo |
| `name` | `Tab A` | nome da mostrare |
| `protocol` | `1` | versione del protocollo |

Il trasmettitore Windows interroga `_altrove._tcp.local` da una porta qualsiasi (non la 5353),
così i ricevitori rispondono direttamente a lui ("legacy unicast", RFC 6762 §6.7).
La stessa domanda va sia al gruppo multicast sia, ogni 30 s, direttamente alla porta 5353 di ogni altro
indirizzo della rete locale (reti private, al massimo la /24 del PC): molti router scartano il multicast tra
due dispositivi Wi-Fi ma non il traffico normale, e Android risponde anche a una domanda diretta. Chi ha
risposto viene poi interrogato direttamente a ogni giro (3 s), così resta nell'elenco. Il ricevitore non deve
fare niente di nuovo: basta l'annuncio mDNS di sempre.
Se la scoperta non funziona si può sempre scrivere l'indirizzo a mano: `192.168.1.20` o `192.168.1.20:47800`.

## Connessione

`ws://<ricevitore>:47800/` — nessun percorso particolare.

Il ricevitore accetta un trasmettitore alla volta: se ne arriva un secondo mentre uno sta già
trasmettendo, il nuovo prende il posto del vecchio (che riceve `bye` con `reason: "replaced"`).
Dalla 0.5 il nuovo prende il posto del vecchio solo dopo il suo `hello` e, se il ricevitore chiede l'abbinamento,
solo dopo `paired`: fino ad allora il ricevitore gli risponde solo a `hello`, `pair` e `ping`.

Il trasmettitore considera persa la connessione anche quando per 8 s non arriva niente dal ricevitore (che
risponde a un `ping` ogni secondo): un Wi-Fi che cade lascia il socket aperto per minuti.
Se la connessione si perde, o il ricevitore se ne va con `bye` e `reason: "away"`, il trasmettitore Windows
riprova da solo per 2 minuti con le stesse scelte (il monitor virtuale di Estendi resta attaccato).

## Messaggi di controllo (testo JSON)

Ogni messaggio ha un campo `type`. I campi sconosciuti vanno ignorati, così le versioni future
possono aggiungerne senza rompere le vecchie.

### `hello` — entrambi, appena aperta la connessione
```json
{ "type": "hello", "protocol": 1, "role": "sender", "id": "…", "name": "PC-FISSO", "app": "0.1.0" }
```
Il ricevitore risponde con il suo `hello`, che dice anche cosa sa mostrare:
```json
{ "type": "hello", "protocol": 1, "role": "receiver", "id": "…", "name": "Tab A", "app": "0.1.0",
  "display": { "width": 1920, "height": 1200, "refreshRate": 60 },
  "codecs": ["h264"], "maxWidth": 1920, "maxHeight": 1080, "audioCodecs": ["opus", "pcm"] }
```
- `display`: risoluzione dello schermo del ricevitore, in pixel reali.
- `maxWidth` / `maxHeight`: il flusso più grande che il decoder regge (in orizzontale).
- `audioCodecs` (dalla 0.4): i formati audio che il ricevitore sa riprodurre; senza, niente audio.

Se `protocol` dell'altro è diverso dal proprio, si chiude con `bye` e `reason: "protocol"`.

#### Funzioni facoltative (`features`)
Nel proprio `hello` ciascun lato può elencare le funzioni facoltative che gestisce:
```json
{ "type": "hello", "protocol": 1, "role": "sender", "id": "…", "name": "PC-FISSO", "app": "0.2.0",
  "features": ["touch", "configure"] }
```
| Funzione | Chi la annuncia | Cosa vuol dire |
|---|---|---|
| `touch` | trasmettitore | accetta i messaggi `touch`: il ricevitore può comandare il PC con il tocco |
| `configure` | trasmettitore | manda `settings` e accetta `configure`: il ricevitore può cambiare modalità e qualità |
| `mouse` | trasmettitore | accetta i messaggi `mouse`: il ricevitore fa da trackpad (dalla 0.3) |
| `scale` | trasmettitore | in `extend` il ricevitore può scegliere la scala (DPI) dello schermo virtuale (dalla 0.3) |
| `audio` | trasmettitore | manda l'audio del PC (se il ricevitore ha `audioCodecs`) e accetta `configure` con `audio` (dalla 0.4) |
| `mic` | trasmettitore | accetta la voce del microfono del ricevitore e la presenta a Windows come «Microfono Altrove» (dalla 0.4) |
| `pairing` | ricevitore | prima di tutto il resto il trasmettitore deve abbinarsi (`pair` → `paired`, vedi sotto) (dalla 0.5) |
| `pairing` | trasmettitore | sa abbinarsi; un ricevitore che chiede l'abbinamento chiude con `bye` `update` chi non lo annuncia (dalla 0.5) |
| `keyboard` | trasmettitore | accetta i messaggi `key`: il ricevitore scrive sul PC (dalla 0.5) |

Un messaggio facoltativo si manda solo se l'altro lato ha annunciato la funzione. Chi non manda
`features` (le versioni 0.1) non ne gestisce nessuna. Le funzioni nuove non cambiano `protocol`.

### Abbinamento: `pair` / `pinRequired` / `paired` (funzione `pairing`, dalla 0.5)
Se l'`hello` del ricevitore ha `pairing`, il trasmettitore manda subito, prima di `start`:
```json
{ "type": "pair", "token": "9f2c…" }
```
con il `token` di un abbinamento precedente con quel ricevitore (chiave: l'`id` del suo `hello`), oppure senza
`token` la prima volta. Il ricevitore risponde:
```json
{ "type": "paired", "token": "9f2c…" }
{ "type": "pinRequired", "wrong": false }
```
- `paired`: il trasmettitore è ammesso; tiene `token` per le prossime volte (32 cifre esadecimali casuali).
- `pinRequired`: il ricevitore mostra un PIN di 6 cifre; il trasmettitore lo chiede all'utente e lo manda con
  `{ "type": "pair", "pin": "482913" }`. Se è sbagliato arriva un altro `pinRequired` con `wrong: true`; al terzo
  sbagliato, o se sul ricevitore si preme «Rifiuta», o dopo 2 minuti, arriva `bye` con `reason: "pairing"`.
  Un `pinRequired` in risposta a un `token` vuol dire che il ricevitore non lo conosce più: il trasmettitore lo dimentica.
- Il ricevitore tiene per ogni trasmettitore (chiave: l'`id` del suo `hello`) solo lo SHA-256 del token.
  Un PIN vale solo per il trasmettitore a cui è stato mostrato, e uno alla volta.
- Chi rinuncia a scrivere il PIN chiude con `bye` `user`.

Il ricevitore Android chiede l'abbinamento salvo che l'utente lo spenga («Chiedi un PIN ai PC nuovi: No»).
Token e PIN viaggiano in chiaro come il resto: proteggono da chi si collega per sbaglio o per dispetto, non da chi
legge il traffico della rete.

### `start` — trasmettitore → ricevitore
```json
{ "type": "start", "codec": "h264", "width": 1920, "height": 1080, "fps": 60, "mode": "mirror" }
```
`mode`: `mirror` (duplica) oppure `extend` (schermo virtuale, dalla fase 2). Dopo `start` arrivano i pacchetti video.
Un nuovo `start` durante la trasmissione (es. risoluzione cambiata) fa ripartire il decoder.

### `keyframe` — ricevitore → trasmettitore
```json
{ "type": "keyframe" }
```
Chiede un fotogramma completo (IDR). Va mandato quando il decoder riparte o perde il filo.

### `ping` / `pong` — entrambi
```json
{ "type": "ping", "t": 123456789 }
{ "type": "pong", "t": 123456789 }
```
`t` è un orologio qualsiasi di chi manda il ping, in millisecondi; il `pong` lo rimanda identico.
Serve a misurare il tempo di andata e ritorno.

### `stats` — ricevitore → trasmettitore, circa una volta al secondo
```json
{ "type": "stats", "fps": 59.8, "decodeMs": 4.1, "dropped": 0 }
```

### `stop` — trasmettitore → ricevitore
```json
{ "type": "stop" }
```
Fine della trasmissione; la connessione può restare aperta per un nuovo `start`.

### `settings` — trasmettitore → ricevitore (funzione `configure`)
```json
{ "type": "settings", "mode": "mirror", "screen": "\\\\.\\DISPLAY1", "maxHeight": 0, "fps": 60, "bitrateKbps": 12000,
  "choices": {
    "modes": ["mirror", "extend"],
    "screens": [{ "id": "\\\\.\\DISPLAY1", "name": "Schermo 1 – 3440×1440" }],
    "maxHeights": [0, 1080, 720], "fps": [60, 30], "bitratesKbps": [6000, 12000, 20000, 30000],
    "resolutions": [{ "maxHeight": 1200, "width": 1920, "height": 1200, "recommended": true },
                    { "maxHeight": 1600, "width": 2560, "height": 1600, "recommended": false }],
    "scales": [100, 125, 150, 175, 200] },
  "scale": 125,
  "notice": "…" }
```
Cosa si sta trasmettendo e cosa il ricevitore può chiedere. Arriva dopo ogni `start` e ogni volta che
qualcosa cambia.
- `maxHeight`: il limite di risoluzione scelto, `0` = automatica (lo schermo del ricevitore).
- `screen`: lo schermo duplicato (in `extend` è quello di prima, per tornare in `mirror`).
- `choices.modes` contiene `extend` solo se sul PC lo schermo virtuale è installato.
- `notice` (facoltativo): un testo per l'utente, es. perché l'ultima richiesta non è stata applicata.
- `choices.resolutions` (dalla 0.3): le risoluzioni possibili adesso, già calcolate per la modalità e lo schermo
  correnti (`width`×`height` è quello che arriverà davvero), dalla più grande alla più piccola. `recommended` è la
  più grande che sta nello schermo del ricevitore. Con `resolutions`, `maxHeight` è quello della risoluzione in uso
  (mai `0`); il ricevitore la chiede con `configure` e `maxHeight`. `maxHeights` resta per i ricevitori 0.2.
- `touch` (dalla 0.4): `false` se sul PC tocco e trackpad sono spenti (i messaggi `touch` e `mouse` vengono ignorati).
- `scale` e `choices.scales` (dalla 0.3, funzione `scale`): la scala di Windows dello schermo virtuale in
  percentuale. Solo in `extend`; in `mirror` `scales` è vuoto e `scale` manca.

### `configure` — ricevitore → trasmettitore (funzione `configure`)
```json
{ "type": "configure", "mode": "extend", "maxHeight": 720 }
```
Solo i campi da cambiare, fra `mode`, `screen`, `maxHeight`, `fps`, `bitrateKbps`, `scale`, con valori presi da
`choices`. Il trasmettitore applica senza chiudere la connessione: manda un nuovo `start` (il
ricevitore fa ripartire il decoder) e un nuovo `settings`. Se non può, rimanda il `settings` di prima
con un `notice`.

### `touch` — ricevitore → trasmettitore (funzione `touch`)
```json
{ "type": "touch", "pointers": [{ "id": 0, "x": 0.4182, "y": 0.625, "state": "down" }] }
```
Un messaggio a ogni cambiamento, con tutte le dita appoggiate in quel momento.
- `x`, `y`: posizione sull'immagine del flusso, da 0 a 1, con 0,0 in alto a sinistra. Non dipende dalla
  risoluzione né dalle bande nere: il ricevitore conta solo i tocchi sull'immagine.
- `id`: lo stesso finché il dito resta appoggiato.
- `state`: `down` (appena appoggiato), `move`, `up` (appena sollevato: compare in questo messaggio e
  poi non più), `cancel` (gesto annullato, per tutte le dita insieme).

Il trasmettitore Windows li trasforma in tocco di Windows sullo schermo trasmesso (in `mirror`) o sul
monitor virtuale (in `extend`): un tocco è un clic, tenere premuto è il clic destro, due dita scorrono e
ingrandiscono dove l'applicazione lo gestisce. Mentre un dito è fermo il trasmettitore ripete da sé
l'ultima posizione, così Windows non considera il tocco interrotto: il ricevitore non deve farlo.

### `mouse` — ricevitore → trasmettitore (funzione `mouse`, dalla 0.3)
```json
{ "type": "mouse", "action": "move", "dx": 0.0125, "dy": -0.004 }
{ "type": "mouse", "action": "down", "button": "left" }
{ "type": "mouse", "action": "up", "button": "left" }
{ "type": "mouse", "action": "click", "button": "right" }
{ "type": "mouse", "action": "scroll", "dx": 0, "dy": 1.5 }
```
Il ricevitore come trackpad: i gesti li riconosce lui, il trasmettitore muove il puntatore e preme i tasti.
- `move`: spostamento relativo del puntatore, in frazioni della larghezza (`dx`) e dell'altezza (`dy`) dell'immagine.
  Il puntatore resta dentro lo schermo trasmesso.
- `down` / `up` / `click`: tasto `left`, `right` o `middle`; `click` è premi e rilascia.
- `scroll`: rotellina in scatti (anche frazionari); `dy` positivo scorre verso l'alto, `dx` positivo verso destra.

Gesti del ricevitore Android: un dito muove; un tocco è un clic; un tocco con due dita è il clic destro; due
dita che scorrono sono la rotellina; doppio tocco tenendo giù il secondo trascina. Il tocco del secondo dito vale anche
se il primo era appoggiato da prima o stava muovendo il puntatore.

### `key` — ricevitore → trasmettitore (funzione `keyboard`, dalla 0.5)
```json
{ "type": "key", "text": "ciao è" }
{ "type": "key", "key": "backspace" }
{ "type": "key", "key": "c", "modifiers": ["ctrl"] }
```
La tastiera del ricevitore sul PC, nella finestra che ha il fuoco.
- `text`: da scrivere così com'è (qualsiasi carattere, anche accenti ed emoji); `\n` è Invio, `\t` è Tab.
- `key`: un tasto premuto e rilasciato: `enter`, `backspace`, `delete`, `tab`, `escape`, `space`, `left`, `right`,
  `up`, `down`, `home`, `end`, `pageUp`, `pageDown`, `win`, oppure una lettera o una cifra (in minuscolo) o un altro
  carattere singolo, per le scorciatoie. `modifiers` (facoltativo): `ctrl`, `alt`, `shift`, `win`, tenuti premuti intorno al tasto.
- Un tasto sconosciuto si ignora. Come `touch` e `mouse`, vale solo se in `settings` `touch` non è `false`.

Il ricevitore Android manda le parole della tastiera sullo schermo man mano: quando la tastiera corregge una parola,
manda tanti `backspace` quanti i caratteri cambiati e poi il resto nuovo.

### `audioStart` / `audioStop` — trasmettitore → ricevitore (funzione `audio`, dalla 0.4)
```json
{ "type": "audioStart", "codec": "opus", "sampleRate": 48000, "channels": 2, "frameMs": 10 }
{ "type": "audioStop" }
```
Dopo `audioStart` arrivano i pacchetti audio (binario, tipo `2`) in quel formato, fino ad `audioStop` o a un
nuovo `audioStart`. `codec` è uno di quelli del `hello` del ricevitore: `opus` (consigliato, ~128 kbit/s) oppure
`pcm` (16 bit little-endian, campioni dei canali alternati, ~1,5 Mbit/s). Il PC manda l'audio che sta suonando
(tutto il mix di Windows). Quando non suona niente non arrivano pacchetti.

In `settings` il campo `audio` dice se l'audio è acceso; il ricevitore lo cambia con `configure`:
```json
{ "type": "configure", "audio": false }
```

### `micStart` / `micStop` — ricevitore → trasmettitore (funzione `mic`, dalla 0.4)
```json
{ "type": "micStart", "codec": "opus", "sampleRate": 48000, "channels": 1, "frameMs": 20 }
{ "type": "micStop" }
```
La voce del microfono del ricevitore verso il PC. Dopo `micStart` arrivano pacchetti binari di tipo `3` in quel
formato (Opus, oppure `pcm` a 16 bit little-endian), fino a `micStop`.

Chi decide è il trasmettitore: in `settings` il campo `mic` vale `null` se il PC non può usare la voce (manca il
componente per il microfono), `false` se è spento, `true` se è acceso. Il ricevitore lo chiede con `configure` e
`mic`, e registra solo finché `settings.mic` è `true`. Sul PC la voce diventa il «Microfono Altrove», un microfono
USB che le app vedono come gli altri (USB/IP in locale con usbip-win2, vedi README).

### `bye` — entrambi
```json
{ "type": "bye", "reason": "user" }
```
Chi lo manda chiude subito dopo. `reason`: `user`, `replaced`, `protocol`, `error`; dalla 0.5 anche `away` (il
ricevitore è passato in secondo piano o ha spento lo schermo: tornerà ad ascoltare, il trasmettitore può riprovare),
`pairing` (abbinamento fallito o rifiutato) e `update` (l'altro lato è troppo vecchio, per esempio senza `pairing`).

## Pacchetti video e audio (binario)

Un messaggio WebSocket binario = un fotogramma compresso o un pezzo di audio:

| Byte | Contenuto |
|---|---|
| 0 | tipo: `1` = video, `2` = audio del PC, `3` = voce del ricevitore (dalla 0.4) |
| 1 | flag (video): bit 0 = fotogramma chiave (IDR), bit 1 = contiene SPS/PPS; audio: 0 |
| 2–9 | istante di presentazione in microsecondi, intero a 64 bit big-endian |
| 10… | video: il fotogramma H.264 in formato Annex B (NAL separati da `00 00 00 01`); audio: un pacchetto Opus o un blocco PCM |

- Ogni fotogramma chiave è preceduto da SPS e PPS nello stesso pacchetto: un ricevitore può
  agganciarsi a qualsiasi IDR.
- Niente B-frame: i fotogrammi arrivano già nell'ordine in cui vanno mostrati.
- Se la rete rallenta, il trasmettitore scarta fotogrammi fino al prossimo IDR invece di accumulare ritardo.
- Dalla 0.5 il trasmettitore Windows abbassa anche il bitrate (di un terzo, al più ogni 2 s, mai sotto un quinto della
  scelta né sotto 1,5 Mbit/s) quando la coda si riempie o i `ping` tornano molto più lenti del loro meglio, e lo
  riporta su un decimo alla volta dopo 5 s buoni, fino a quello scelto. Il ricevitore non deve fare niente: `settings`
  continua a dire il bitrate scelto.
- L'audio passa prima del video. Un ricevitore che non conosce un tipo ignora il pacchetto.
- Il ricevitore suona l'audio appena arriva, con una piccola riserva: se ne accumula troppo (oltre ~150 ms) scarta il vecchio, come il video.
