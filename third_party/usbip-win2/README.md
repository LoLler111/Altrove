# usbip-win2 0.9.8.1 (la parte che serve ad Altrove)

Da [usbip-win2](https://github.com/vadimgrn/usbip-win2) di Vadym Hrynchyshyn, release
[v.0.9.8.1](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.1), file non modificati:

- `usbip2_ude.inf/.sys/.cat`: il controller USB emulato (`ROOT\USBIP_WIN2\UDE`), firmato da Microsoft.
- `usbip2_filter.inf/.sys/.cat`: il filtro per gli hub USB 3 che lo accompagna, firmato da Microsoft.
- `usbip.exe` (firmato), `libusbip.dll`, `resources.dll`: lo strumento con cui Altrove collega il proprio microfono.

Il catalogo `.cat` firma i byte esatti dell'INF: non modificarli (`.gitattributes` li tiene fuori dalla conversione
dei fine riga). Altrove li installa come fa l'installatore originale (`UsbIpSetup.Install`: prima il filtro, poi il
nodo radice con il driver UDE). Per cambiare versione sostituire tutti i file della stessa release.

Licenza BSD-2-Clause, testo in `LICENSE.txt` (Copyright (c) 2021-2026 Vadym Hrynchyshyn). Sorgenti: <https://github.com/vadimgrn/usbip-win2>.
