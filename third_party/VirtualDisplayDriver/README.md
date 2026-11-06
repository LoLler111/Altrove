# Virtual Display Driver (MttVDD)

Il driver IddCx che crea il monitor virtuale per la modalità Estendi.

- Progetto: https://github.com/VirtualDrivers/Virtual-Display-Driver (licenza MIT, vedi `LICENSE`)
- Versione: release 25.7.23, file `VirtualDisplayDriver-x86.Driver.Only.zip` (driver 11.30.4.434, nonostante il nome è x64)
- SHA-256 dello zip: `\e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a`
- Firma: `MttVDD.dll` e `mttvdd.cat` firmati da SignPath Foundation, quindi si installano senza test-signing.
- Hardware ID: `Root\MttVDD`

Il file `vdd_settings.xml` non viene da qui: Altrove lo scrive da sé (`src/Altrove.Capture/VirtualDisplay`).
Per aggiornare il driver: sostituire i tre file, aggiornare versione e SHA qui sopra.
