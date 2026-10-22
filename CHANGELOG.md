# Changelog

Alle nennenswerten Änderungen an SyncDrive. Format nach [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionen nach
[Semantic Versioning](https://semver.org/lang/de/). Vor 1.0.0 darf sich das Protokoll noch ändern; jeder Meilenstein hebt MINOR, reine
Korrekturen heben PATCH. Die Version steht an einer Stelle (`04-umsetzung/Directory.Build.props`); ein Test prüft, dass sie mit dem obersten
Eintrag hier übereinstimmt. Jede Version bekommt ein Git-Tag `vX.Y.Z`. Die Versionen 0.1.0 bis 0.11.0 wurden nachträglich vergeben.

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
