# Changelog

Alle nennenswerten Änderungen an SyncDrive. Format nach [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionen nach
[Semantic Versioning](https://semver.org/lang/de/). Vor 1.0.0 darf sich das Protokoll noch ändern; jeder Meilenstein hebt MINOR, reine
Korrekturen heben PATCH. Die Version steht an einer Stelle (`04-umsetzung/Directory.Build.props`); ein Test prüft, dass sie mit dem obersten
Eintrag hier übereinstimmt. Jede Version bekommt ein Git-Tag `vX.Y.Z`. Die Versionen 0.1.0 bis 0.11.0 wurden nachträglich vergeben.

## [0.31.0] – 2026-10-22 – Hell und Dunkel

### Hinzugefügt
- Weboberfläche: **Design Hell, Dunkel oder System** im Profilmenü. „System“ folgt der Einstellung des Betriebssystems, auch wenn sie
  sich ändert; die Wahl gilt je Browser. Die dunklen Werte stehen als zweiter Token-Block in `app.css` (`:root[data-theme="dark"]`),
  gesetzt vor dem ersten Zeichnen, damit nichts hell aufblitzt. Ein Architekturtest prüft, dass der dunkle Block nur vorhandene Tokens überschreibt.

## [0.30.0] – 2026-10-22 – Profilbild

### Hinzugefügt
- Weboberfläche: eigenes **Profilbild**. Im Profilmenü auf das Bild klicken oder „Profilbild ändern“ wählen (JPEG, PNG oder WebP bis
  5 MB); „Profilbild entfernen“ kehrt zum Anfangsbuchstaben zurück. Der Server speichert nur eine verkleinerte Fassung (höchstens 256 px,
  JPEG) unter `/data/avatars`, die tägliche Sicherung schließt sie ein. API: `GET`/`PUT`/`DELETE /api/v1/account/avatar` (nur das eigene Bild).

## [0.29.0] – 2026-10-22 – Zahnrad für die Verwaltung

### Geändert
- Weboberfläche: Administratoren haben in der linken Leiste über dem Profil ein **Zahnrad** mit „Organisationseinstellungen“ (Reiter
  Richtlinien, Team-Ordner), „Benutzer & Gruppen verwalten“ (Reiter Benutzer, Gruppen) und „Anmeldeseite“. Das Profilmenü enthält nur noch
  Persönliches (Geräte, Abmelden). Das Zahnrad sehen nur Administratoren; auf schmalen Bildschirmen passt sich das Menü der Breite an.

## [0.28.0] – 2026-10-22 – Menü „Neu“ mit Ordner-Upload

### Hinzugefügt
- Weboberfläche, Menü **Neu**: „Neuer Ordner“, „Dateien hochladen“ und „Ordner hochladen“ mit Symbolen und Tastenkürzeln (Alt+C, dann F, U
  oder I). „Ordner hochladen“ übernimmt die Struktur des gewählten Ordners samt Unterordnern (`BrowserViewModel.UploadFolderAsync`; die Pfade
  liefert der Browser über `webkitRelativePath`, `wwwroot/js/syncdrive.js`).
- Die Schaltfläche „Hochladen“ in der Werkzeugleiste bleibt; beide nutzen dasselbe Dateifeld.

## [0.27.0] – 2026-10-22 – Design-System überall, Drag & Drop

### Geändert
- Farben kommen überall aus den Tokens des Design-Systems „SyncDrive“: Weboberfläche nur über CSS-Variablen in `:root` (Namen wie im
  Design-System, z. B. `--accent`, `--hover-surface`, `--filetype-pdf`), Desktop-Client nur über Ressourcen in `App.axaml`. Neu dazu, weil
  das Design-System sie noch nicht kennt: `on-accent` (Schrift auf Teal und Dunkel; trennt sie von der Fläche `bg`, Grundlage für ein
  dunkles Theme), `selected-row`, `selected-row-hover`, `icon-disc`, `scrim` sowie die Statusfarben `status-ok`, `status-idle`, `status-off`.
- Dateisymbole im Desktop-Client haben jetzt dieselben Farben wie im Web (`filetype-*`; vorher eigene, leicht abweichende Töne).
- Ein Architekturtest verhindert feste Farbwerte außerhalb der Token-Definitionen (CSS, Razor außer dem Logo, AXAML).

### Behoben
- Drag & Drop in der Weboberfläche: Die Ablagefläche schloss sich, sobald der Mauszeiger auf das (unsichtbare) Dateifeld darin wechselte,
  die Datei fiel ins Leere; bei gescrollter Liste lag die Fläche zudem außerhalb des sichtbaren Bereichs. Die Fläche liegt jetzt über dem
  sichtbaren Teil der Liste und schließt erst beim Verlassen; eine daneben fallengelassene Datei öffnet der Browser nicht mehr selbst.

## [0.26.0] – 2026-10-22 – Vollständige Deinstallation

### Geändert
- Deinstallation des Windows-Clients räumt alles Benutzerbezogene ab (nicht bei Updates): Der Installer ruft vor dem Entfernen der
  Dateien `syncdrive-daemon --uninstall` auf. Das beendet Oberfläche und Daemon, meldet jeden Sync-Ordner bei Windows ab (Platzhalter ohne
  Inhalt verschwinden, alles andere wird zur gewöhnlichen Datei), löscht den Arbeitsordner `.syncdrive`, meldet übrige Sync-Roots ab
  (`syncdrive-cloudreg unregister-all`), entfernt Explorer-Menü, Autostart, alle SyncDrive-Einträge im Navigationsbereich und
  `HKCU\Software\SyncDrive` und zuletzt `%LOCALAPPDATA%\SyncDrive` (Konfiguration, Anmeldedaten, Logs). Die synchronisierten Dateien bleiben.
  Ein gescheiterter Schritt hält die übrigen nicht auf.

### Behoben
- Windows legt den Navigationseintrag eines Cloud-Ordners in der 64- und der 32-Bit-Ansicht der Registry an und entfernt beim Abmelden nur
  die 64-Bit-Kopie. Deinstallation und die echten Cloud-Files-Tests räumen jetzt beide Ansichten auf (die Tests hatten Reste hinterlassen).

## [0.25.0] – 2026-10-22 – Einfachere Einrichtung des Clients

### Geändert
- Assistent: Wählt man keinen eigenen lokalen Ordner, legt der Client `%USERPROFILE%\SyncDrive` an (ist er schon einer Aufgabe zugeordnet,
  `SyncDrive 2`, `SyncDrive 3` …); die Seite zeigt ihn vorab mit „(wird automatisch angelegt)“.
- „Erweiterte Konsistenzprüfung“ ist keine Option mehr (Assistent und Sync-Regeln): neue Aufgaben, auch per `syncdrive_cli`, prüfen immer
  den Inhalt; wer die Sync-Regeln einer bestehenden Aufgabe speichert, schaltet sie ebenfalls ein.
- Der Arbeitsordner `.syncdrive` im Sync-Ordner ist unter Windows versteckt (bestehende Sync-Ordner beim nächsten Start).

### Behoben
- Seite „Logs“: über dem Filter stand „System.InvalidCastException: Could not convert '(null)' … KeyValuePair“. Beim Neuladen leert die
  Auswahlbox ihre Liste und meldet „nichts gewählt“; Aufgaben-, Ereignis- und Sprachauswahl nehmen das jetzt an. Ein Render-Test lädt die
  Seite neu und prüft die Auswahlboxen auf Fehler (ohne Korrektur schlägt er mit genau dieser Meldung fehl).

## [0.24.0] – 2026-10-22 – Neue Anmeldeseite

### Geändert
- Anmeldeseite der Weboberfläche nach dem Aufbau von Nextcloud (eigene Gestaltung): mittig das Logo, darunter eine halbtransparente Karte
  „Bei <Servername> anmelden“ mit Name **und** Passwort auf einer Seite und einer breiten Schaltfläche „Anmelden →“; der Text des
  Administrators steht unter der Karte, unten eine kurze Fußzeile. Hintergrundbild bzw. Verlauf bleiben einstellbar.
- Der zweistufige Ablauf (erst Name, dann Passwort) entfällt samt `SignInStep`; der Test prüft die Anmeldung in einem Schritt.

## [0.23.0] – 2026-10-22 – „Link erhalten“ im Explorer

### Hinzugefügt
- Windows-Client, Explorer-Untermenü **SyncDrive → Link erhalten** (Dateien und Ordner): nimmt den vorhandenen öffentlichen Link oder legt
  einen ohne Passwort und Ablauf an, kopiert die Adresse in die Zwischenablage und zeigt sie an. Die Adresse lautet wie die Serveradresse
  des Clients plus `/s/<token>`.
- Schreiben die Freigabe-Richtlinien Passwort oder Ablaufdatum vor, erklärt die Meldung, dass der Link in der Weboberfläche anzulegen ist;
  ohne Freigaberecht oder bei abgeschalteten Links: „Nicht erlaubt“.
- Daemon: `IShellActions.GetLinkAsync` (IPC `shell.link`), `IServerAccess.GetOrCreateLinkAsync`; Oberfläche: `CopyAndShowAsync`.
- Tests: Menü, Explorer-Befehl, Daemon (Pfad samt Aufgaben-Unterordner, Richtlinie), Ende-zu-Ende gegen den echten Server (zweimal derselbe
  Link, die Linkseite antwortet).

## [0.22.0] – 2026-10-22 – Speicher und Kontingent im Konto-Menü

### Hinzugefügt
- `GET /api/v1/account/storage`: eigener belegter Speicher (Spaces, die dem Benutzer gehören) und das Kontingent; dieselbe Rechnung wie
  in der Verwaltung.

### Geändert
- Weboberfläche, Konto-Menü: mit Kontingent „293 KB von 1 GB belegt“ samt Balken (rot ab 90 %), ohne Kontingent wie bisher „… belegt“.
  Der Browser summiert nicht mehr selbst über die Spaces.
- Das Symbol vor „Labels“ ist ein eigenes SVG-Etikett in Textfarbe statt des kaum erkennbaren Emojis.

## [0.21.1] – 2026-10-22 – Kompaktere Leisten

### Geändert
- Weboberfläche: Die Benachrichtigungsglocke sitzt oben in der linken Leiste direkt unter dem Logo; die Benachrichtigungen klappen daneben
  nach unten auf. Die Werkzeugleiste ist kompakter (engere Abstände und Polster, 13 px Schrift, kein Umbruch innerhalb einer Schaltfläche)
  und bleibt bei 1024 px Breite einzeilig.

## [0.21.0] – 2026-10-22 – Freigabe und Link in einem

### Geändert
- Weboberfläche: „Link holen“ (Werkzeugleiste und Kontextmenü) entfällt; öffentliche Links legt man im Dialog **Freigeben** im Reiter
  „Öffentlicher Link“ an. Neben dem Link steht ein kleines Kopier-Symbol (⧉, Tooltip „Link kopieren“) statt der Schaltfläche „Kopieren“.
- Dateien öffnen den Freigabedialog direkt im Reiter „Öffentlicher Link“ (Dateien lassen sich nur per Link teilen).

### Entfernt
- `ShareViewModel.EnsureLinkAsync`/`ShowLinkCopied` und der Text „Link holen“; Tests legen Links jetzt wie der Dialog an.

## [0.20.0] – 2026-10-22 – Anheften im SyncDrive-Menü

### Geändert
- „Immer auf diesem Gerät behalten“ und „Speicherplatz freigeben“ stehen im Explorer-Untermenü **SyncDrive** (für Dateien und Ordner, nur in
  Aufgaben mit Dateien auf Abruf) statt als Windows-Einträge oben im Kontextmenü. Die Sync-Ordner werden dafür mit `AllowPinning = false`
  angemeldet; der Daemon setzt den Pin-Zustand (`CfSetPinState`, bei Ordnern rekursiv), Holen und Freigeben bleibt beim `PinWatcher`.
  Erfolg zeigt der Explorer am Statussymbol; nur Fehler erscheinen als Meldung.
- Bestehende Sync-Ordner übernehmen die Änderung beim nächsten Start des Clients (die Anmeldung wird bei jedem Start erneuert).
- `docs/client.md`: Abschnitt „Explorer menu“.

### Geprüft
- Echter Cloud-Files-Test unter Windows (mit Stefans OK): Anheften eines Ordners holt den Inhalt, Freigeben macht die Datei wieder „nur
  online“, und das Kontextmenü enthält die Windows-Einträge nicht mehr. Gegenprobe ohne `AllowPinning = false`: Windows zeigt „Always keep
  on this device“ und „Free up space“.

## [0.19.0] – 2026-10-22 – Dateisperren entfernt

### Entfernt
- Dateisperren vollständig (Stefan: kein Nutzen): Explorer-Menü „Datei sperren“, „Entsperren“, „Entsperren anfragen“; Server-Endpunkte
  `/spaces/{s}/locks` und `/locks/request`; Fehlercode `locked` (423), Feed-Eintrag `lock`, Feld `lockedBy` in Knoten; Benachrichtigung
  `unlock_request` in Client und Web; die Sonderbehandlung im Client (Änderung an fremd gesperrter Datei als Konfliktkopie).

### Geändert
- Protokoll: Clients dieser Version sprechen nur mit Servern ab 0.19.0 vollständig (Sperr-Aufrufe gibt es nicht mehr).
- Bestehende Datenbanken werden beim Start bereinigt: Feed-Einträge `lock` werden zu `upsert`, Bitten um Entsperren verschwinden, die Spalten
  `locked_by`/`locked_at` fallen weg. Ein Test prüft die Migration an einer Datenbank im alten Stand.
- Der Test für Benachrichtigungen prüft jetzt die Freigabe-Meldung samt „als gelesen markieren“.

## [0.18.7] – 2026-10-22 – Compose ohne .env

### Behoben
- Das Compose-Beispiel brach ohne `.env` ab (Pflichtvariable `SYNCDRIVE_ADMIN_PASSWORD`); Portainer und NAS-Oberflächen lesen keine `.env`.
  Alle Werte stehen jetzt direkt in `docker/docker-compose.yml` (Port `8080:8080`, Passwort ersetzen), `.env.example` entfällt.

## [0.18.6] – 2026-10-22 – Schlankes README, Kubernetes und Ansible

### Hinzugefügt
- `kubernetes/`: Manifeste (Namespace, Volumes, Deployment mit einer Replik, Service auf 8080) und ein Ingress-Beispiel mit TLS; in einem
  k3s-Testcluster ausgerollt und angemeldet.
- `ansible/`: Playbook, das den Dienst als Docker-Container ausrollt (Volumes, Container, Warten auf „healthy“), mit Beispiel-Inventar;
  gegen Docker getestet.

### Geändert
- README nach dem Muster von passbolt_docker: kurze Einleitung, Voraussetzungen, Docker Compose und `docker run`, Variablentabelle,
  deutlicher Download-Link für den Windows-Client, Kubernetes, Ansible und Bauen aus dem Quellcode als nachgelagerte Wege. Entfernt:
  Komponenten, technische Funktionsdetails, Versionierung.
- `docs/server.md` ist nur noch die Betriebsreferenz (HTTPS, Variablen, Sicherung, Aktualisieren, Fehlersuche).

### Behoben
- Der Download-Link `releases/latest` lieferte 404, solange alle Releases Vorabversionen sind; er zeigt jetzt auf die Release-Übersicht.

## [0.18.5] – 2026-10-22 – Reiner Dienst ohne Proxy

### Geändert
- Das Compose-Beispiel in `docker/` startet nur den SyncDrive-Dienst, ohne Caddy: Port 8080 (HTTP), Administrator-Passwort aus `.env`.
- `docs/server.md` neu gegliedert: Docker Compose, `docker run`, HTTPS wahlweise direkt im Container (Zertifikat, Port 8443, ohne Proxy)
  oder hinter einem Reverse-Proxy; Tabelle der Ports und der Kestrel-Variablen für Zertifikate.
- „Building from source“ aus dem README nach `docs/development.md` verschoben (Bauen, Tests, lokaler Start, Projektstruktur, Releases).
- README und `docs/client.md`: Serveradresse für HTTP mit `http://` und Port angeben (ohne Schema nimmt der Client HTTPS).

## [0.18.4] – 2026-10-22 – Compose-Beispiel

### Hinzugefügt
- Ordner `docker/` mit einem lauffähigen Compose-Beispiel aus dem Docker-Hub-Image (`docker-compose.yml`, `Caddyfile`, `.env.example`,
  README mit Aufbau, Bereitstellung, Betrieb und Anpassungen). `.env` und `admin-password.txt` dort sind von Git ausgeschlossen.

## [0.18.3] – 2026-10-22 – Betriebsanleitung Docker

### Geändert
- `docs/server.md` als Schritt-für-Schritt-Anleitung: Docker Compose mit TLS aus dem fertigen Image (ohne Quellcode), `docker run` hinter
  eigenem Proxy, Bau aus dem Quellcode, Aktualisieren, Fehlersuche. README mit Schnelltest per `docker run` und Docker-Hub-Badge.
- `deploy/docker-compose.yml` nimmt standardmäßig das Image `magpeek/syncdrive-server:${SYNCDRIVE_VERSION:-latest}`; `--build` baut weiterhin
  aus dem Quellcode.

### Behoben
- Die Anleitung legte die Passwortdatei mit `chmod 600` für den eigenen Benutzer an; der Server (UID 1654) konnte sie unter Linux nicht lesen
  und startete nicht. Jetzt `chown 1654` und `chmod 400`.

## [0.18.2] – 2026-10-22 – Docker Hub

### Geändert
- Das Server-Image erscheint auf Docker Hub als `magpeek/syncdrive-server` mit den Tags `<version>` und `latest` (statt in der GitHub
  Container Registry). Die Pipeline braucht dafür das Repository-Secret `DOCKERHUB_TOKEN`.

### Behoben
- Die Tests ließen sich nur im Debug-Build übersetzen: im Release-Build (CI) brach eine Nullable-Warnung in `ClientSetupTests` den Bau ab.

## [0.18.1] – 2026-10-22 – Veröffentlichung

### Hinzugefügt
- README (englisch) mit Bildern der Weboberfläche, `docs/server.md` (Docker, alle Variablen, Proxy, Sicherung), `docs/client.md`
  (Installation, stille Installation, Kommandozeile, Deinstallation), MIT-Lizenz.
- Release-Workflow: ein Tag `vX.Y.Z` baut die Windows-Pakete und das Server-Image (`linux/amd64`, `linux/arm64`, GHCR) und legt das Release an.

### Geändert
- Das Server-Image kompiliert auf der Build-Plattform für die Zielarchitektur (schnelle arm64-Images ohne Emulation).

### Behoben
- Ein Ende-zu-Ende-Test des Explorer-Menüs prüfte den Dateistatus, bevor der Client seine Basis fortgeschrieben hatte, und scheiterte
  unter Last (Linux, Gesamtlauf) mit „Syncing“ statt „UpToDate“.

## [0.18.0] – 2026-10-22 – Windows-Pakete und Kommandozeile

### Hinzugefügt
- `syncdrive_cli.exe`: Kommandozeile für den Client (`connect`, `connections`, `ls`, `sync`, `desync`, `tasks`, `status`, `pause`, `resume`,
  `version`). Spricht wie die Oberfläche über die Pipe mit dem Daemon und startet ihn bei Bedarf; Rückgabe 0/1/2 für Skripte.
- Windows-Installer `SyncDrive-Client-<Version>-<Build>-x64.msi` und Setup-Programm `….exe` (WiX 5): Installation pro Benutzer ohne
  Administratorrechte nach `%LOCALAPPDATA%\Programs\SyncDrive`, mit eigener .NET-Laufzeit, Startmenü, `syncdrive_cli` im PATH, Start nach der
  Installation (`LAUNCHAPP=0` schaltet ihn ab). Beim Entfernen verschwinden Autostart und Explorer-Menü; Einstellungen und Daten bleiben.
- `installer/build-client.ps1` baut beide Pakete samt SHA-256-Prüfsummen.

### Geändert
- Der Daemon findet `syncdrive-cloudreg.exe` auch direkt neben sich (Installation in einem Ordner).

## [0.17.0] – 2026-10-22 – M7.6 Vorschaubilder

### Hinzugefügt
- Vorschaubilder für JPEG, PNG, GIF, WebP und BMP (`GET /api/v1/spaces/{s}/thumbnail?path=&size=small|large`, 128 bzw. 1024 px):
  beim ersten Abruf erzeugt, nach Inhalt zwischengespeichert, Fotos nach EXIF-Ausrichtung aufgerichtet. Auch Vorschau-Rechte (Previewer)
  sehen sie. Höchstens zwei Bilder werden gleichzeitig gerechnet, Dateien über 50 MB bekommen keine Vorschau.
- Weboberfläche: Bilder zeigen in der Liste ihr Vorschaubild statt des Symbols; ein Doppelklick öffnet sie groß mit „Herunterladen“, Escape schließt.

### Technik
- Neues Adapterprojekt `Infra.Imaging` mit SkiaSharp (MIT); im Container ohne zusätzliche Systempakete.

## [0.16.0] – 2026-10-22 – M7.5 Richtlinien

### Hinzugefügt
- Freigabe-Richtlinien in der Verwaltung (Reiter „Richtlinien“): öffentliche Links und Dateianfragen an oder aus, Passwort vorschreiben,
  Gültigkeit auf eine Höchstzahl von Tagen begrenzen (`GET /api/v1/policies`, `PUT /api/v1/admin/policies`).
- Der Server setzt sie beim Anlegen und Ändern von Links durch; eine abgeschaltete Linkart sperrt sofort auch schon verschickte Links.
- Die Weboberfläche richtet sich danach: „Link holen“ und „Dateien anfordern“ verschwinden, wenn abgeschaltet; vorgeschriebenes Passwort und
  Ablaufdatum sind im Freigabedialog fest angehakt, das Datum ist begrenzt; „Link holen“ öffnet dann gleich den Link-Reiter.

## [0.15.0] – 2026-10-22 – M7.4 Härtung: Blöcke je Space

### Geändert (Protokoll, nicht abwärtskompatibel)
- `POST /blocks/missing` und `GET /blocks/{hash}` gibt es nur noch je Space: `/spaces/{s}/blocks/missing` und `/spaces/{s}/blocks/{hash}`.
  Desktop-Client und Weboberfläche nutzen die neuen Wege; ältere Clients müssen aktualisiert werden.

### Sicherheit
- Wer nur den Hash einer fremden Datei kennt, erfährt nicht mehr, ob es sie gibt, und kommt nicht an ihren Inhalt: ein Space gibt nur
  Blöcke seiner eigenen Dateien und Versionen heraus, und ein Commit nimmt nur solche oder selbst hochgeladene Blöcke.
- Vorschau-Rechte (Previewer) können keine Blöcke mehr laden.

### Geändert
- Dieselbe Datei in zwei Spaces wird zweimal übertragen; gespeichert wird sie weiterhin nur einmal.
- Bestehende Datenbanken bekommen beim Start einmalig die Blockverweise aller Dateien und Versionen.

## [0.14.0] – 2026-10-22 – M7.3 Härtung: Bremse für Passwortversuche

### Sicherheit
- Zu viele falsche Passwörter werden gebremst (`429 too_many_attempts` mit `Retry-After`), bevor das Passwort geprüft wird: 5 Fehlversuche
  je Benutzer und Adresse, 30 je Adresse, 50 je Benutzer in 15 Minuten. Gilt für die Anmeldung von Geräten und im Browser.
- Link-Passwörter: 10 Fehlversuche je Link und Adresse, 100 je Link.
- Desktop-Client und Weboberfläche zeigen dafür eine eigene Meldung.

## [0.13.0] – 2026-10-22 – M7.2 Gruppen und Team-Ordner

### Hinzugefügt
- Benutzergruppen: Administratoren legen Gruppen an, löschen sie und haken Mitglieder an oder ab (`/api/v1/admin/groups`).
- Team-Ordner: Spaces ohne Besitzer, die Administratoren anlegen, umbenennen und Benutzern oder Gruppen mit Rolle (Vorschau bis
  Verwalter) zuweisen (`/api/v1/admin/team-folders`). Neue Mitglieder bekommen die Benachrichtigung „geteilt“.
- Verwaltung mit Reitern Benutzer, Gruppen, Team-Ordner.

### Geändert
- Die wirksame Rolle in einem Space ist die höchste aus direkter Mitgliedschaft und den Gruppen des Benutzers; das gilt überall
  (Rechteprüfung, Spaces-Liste, Freigaben, Desktop-Client).
- Die Seitenleiste liest die Spaces neu, wenn man die Verwaltung verlässt oder eine Benachrichtigung „geteilt“ für einen unbekannten Space kommt.
- Bestehende Datenbanken bekommen beim Start die Art des Space (`personal`/`team`).

## [0.12.0] – 2026-10-21 – M7.1 Admin-Konsole

### Hinzugefügt
- Admin-Konsole: Benutzer anlegen, sperren und entsperren, Admin-Recht vergeben, Passwort neu setzen, Speicherkontingent (`/api/v1/admin/users`).
- Speicherkontingent je Benutzer: Hochladen und Kopieren über das Kontingent scheitern mit `507 quota_exceeded`; gezählt wird nur der Zuwachs.
- Produktversion an einer Stelle; `GET /api/v1/info` liefert sie, Desktop-Client und Browser melden sie in der Geräteliste, „Info“ im Client zeigt sie.

### Geändert
- Gesperrte Benutzer und neue Passwörter melden alle Geräte des Benutzers ab.
- Bestehende Datenbanken bekommen beim Start die Spalten für Kontingent und Anlagedatum.

## [0.11.0] – 2026-10-21 – M7 Betrieb

### Hinzugefügt
- Tägliche Sicherung im Server: konsistenter Datenbank-Stand (`VACUUM INTO`), Blöcke inkrementell, Anmeldeseite; die letzten 7 Stände bleiben.
- Compose gehärtet: Image-Dateisystem nur lesbar, ohne Capabilities, Grenzen für Speicher, CPU und Prozesse, rotierende Logs, Sicherungsvolume.

### Geändert
- SQLite mit Verbindungs-Pooling (gemessen 0,3–0,45 ms schneller je Lesezugriff).

### Entschieden
- Kein Redis und kein Datenbank-Container im Einzelserver (ADR-13, mit Messwerten).

## [0.10.0] – 2026-10-21 – M6b Freigabe

### Hinzugefügt
- Einladungen auf Ordner mit Rolle; der geteilte Ordner erscheint beim Eingeladenen als eigener Space und lässt sich auch mit dem Desktop-Client synchronisieren (ADR-12).
- Öffentliche Links mit Passwort und Ablaufdatum, Dateianfragen (Hochladen ohne Konto, nie überschreiben).
- Web: Freigabedialog, „Link holen“, „Dateien anfordern“, Kategorien Mit mir geteilt / Von mir geteilt / Dateianfragen, Seite `/s/{token}`.

## [0.9.0] – 2026-10-21 – M6.3

### Hinzugefügt
- Favoriten, Labels, Benachrichtigungen, Konto-Menü mit Speicher und Geräteliste („Unlink“), ZIP-Download, Kategorien auf schmalen Bildschirmen.

## [0.8.0] – 2026-10-21 – M6.2

### Hinzugefügt
- Papierkorb, Versionen, Suche mit Bereich, „Zuletzt“, „Speicherort öffnen“; Anmeldeseite mit Servername, Text und Hintergrund.

### Behoben
- „Gespeichert von“ zeigte die interne Benutzer-ID.

## [0.7.0] – 2026-10-21 – M6.1 Weboberfläche

### Hinzugefügt
- Weboberfläche mit Blazor WebAssembly aus dem Server-Container (ADR-11): Anmeldung, Dateibrowser, Hochladen, Herunterladen, Live-Aktualisierung.

## [0.6.0] – 2026-10-21 – M5c On-Demand

### Hinzugefügt
- Dateien auf Abruf und Statussymbole über die Cloud-Files-API, Shell-Anmeldung der Sync-Ordner (ADR-10).

## [0.5.0] – 2026-10-21 – M5b Explorer-Integration (Teil)

### Hinzugefügt
- Explorer-Kontextmenü, Dateistatus, Versionen, Sperren, Ordner ab- und anwählen.

## [0.4.0] – 2026-10-21 – M5 Desktop-Oberfläche

### Hinzugefügt
- Tray, Hauptfenster, Assistent, Einstellungen (Avalonia), Daemon mit Aufgabenüberwachung, IPC über Named Pipe, DPAPI-Geheimnisse.

## [0.3.0] – 2026-10-19 – M4b Server-Erweiterungen

### Hinzugefügt
- Versionen, Kopieren, Server-Papierkorb, Sperren, Rollen, Geräteliste, Favoriten, Labels, Benachrichtigungen.

## [0.2.0] – 2026-10-19 – M4 Mehrere Clients

### Hinzugefügt
- Konfliktbehandlung, Papierkorb, Verlauf, Umbenennungserkennung, Aufgaben und Standardfilter.

## [0.1.0] – 2026-10-19 – M0–M3

### Hinzugefügt
- Gerüst mit Architekturtests, Fachkern (Merge, Filter, Konfliktnamen, Blockzerleger), Server (Spaces, Commit, Feed, Blöcke, Anmeldung,
  Container) und Client-Engine (Scan, Watcher, Hoch- und Herunterladen).
