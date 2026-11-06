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
Se la scoperta non funziona si può sempre scrivere l'indirizzo a mano: `192.168.1.20` o `192.168.1.20:47800`.

## Connessione

`ws://<ricevitore>:47800/` — nessun percorso particolare.

Il ricevitore accetta un trasmettitore alla volta: se ne arriva un secondo mentre uno sta già
trasmettendo, il nuovo prende il posto del vecchio (che riceve `bye` con `reason: "replaced"`).

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
  "codecs": ["h264"], "maxWidth": 1920, "maxHeight": 1080 }
```
- `display`: risoluzione dello schermo del ricevitore, in pixel reali.
- `maxWidth` / `maxHeight`: il flusso più grande che il decoder regge (in orizzontale).

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

Un messaggio facoltativo si manda solo se l'altro lato ha annunciato la funzione. Chi non manda
`features` (le versioni 0.1) non ne gestisce nessuna. Le funzioni nuove non cambiano `protocol`.

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
    "maxHeights": [0, 1080, 720], "fps": [60, 30], "bitratesKbps": [6000, 12000, 20000, 30000] },
  "notice": "…" }
```
Cosa si sta trasmettendo e cosa il ricevitore può chiedere. Arriva dopo ogni `start` e ogni volta che
qualcosa cambia.
- `maxHeight`: il limite di risoluzione scelto, `0` = automatica (lo schermo del ricevitore).
- `screen`: lo schermo duplicato (in `extend` è quello di prima, per tornare in `mirror`).
- `choices.modes` contiene `extend` solo se sul PC lo schermo virtuale è installato.
- `notice` (facoltativo): un testo per l'utente, es. perché l'ultima richiesta non è stata applicata.

### `configure` — ricevitore → trasmettitore (funzione `configure`)
```json
{ "type": "configure", "mode": "extend", "maxHeight": 720 }
```
Solo i campi da cambiare, fra `mode`, `screen`, `maxHeight`, `fps`, `bitrateKbps`, con valori presi da
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

### `bye` — entrambi
```json
{ "type": "bye", "reason": "user" }
```
Chi lo manda chiude subito dopo. `reason`: `user`, `replaced`, `protocol`, `error`.

## Pacchetti video (binario)

Un messaggio WebSocket binario = un fotogramma compresso:

| Byte | Contenuto |
|---|---|
| 0 | tipo: `1` = video |
| 1 | flag: bit 0 = fotogramma chiave (IDR), bit 1 = contiene SPS/PPS |
| 2–9 | istante di presentazione in microsecondi, intero a 64 bit big-endian |
| 10… | il fotogramma H.264 in formato Annex B (NAL separati da `00 00 00 01`) |

- Ogni fotogramma chiave è preceduto da SPS e PPS nello stesso pacchetto: un ricevitore può
  agganciarsi a qualsiasi IDR.
- Niente B-frame: i fotogrammi arrivano già nell'ordine in cui vanno mostrati.
- Se la rete rallenta, il trasmettitore scarta fotogrammi fino al prossimo IDR invece di accumulare ritardo.
