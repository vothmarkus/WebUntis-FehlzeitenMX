# WebUntis-FehlzeitenMX

Windows-App für die Anwesenheitsmatrix in WebUntis am Berufskolleg für Technik Düren. Die App zeigt Fehlzeiten, Entschuldigungen und Verspätungen und exportiert Namenslisten für den Notenrechner.

## Installation und Updates

Lade die Datei `WebUntis-FehlzeitenMX-…-Setup-x64.exe` aus den [GitHub-Releases](https://github.com/vothmarkus/WebUntis-FehlzeitenMX/releases) und starte das Setup. Eine vorhandene Installation wird aktualisiert. Voraussetzung ist Windows 10 ab Version 1809 oder Windows 11, jeweils x64.

Ab Version 2.7 prüft die App beim ersten Öffnen der Einstellungen, ob es auf GitHub ein neues stabiles Release gibt. Ein Klick auf **Update auf … installieren** lädt und prüft das Setup und öffnet es. Öffentliche Releases lassen sich ohne Konto herunterladen.

## Verwendung

Melde dich unter **Einstellungen** bei WebUntis an und wähle deine Klassen. In der **Tagesmatrix** zeigt ein Klick auf eine Zelle die Details zum gewählten Tag. Das rote **×** neben dem Ladebalken bricht eine laufende Übertragung ab. Eine Schülerliste wird beim ausdrücklich gewählten CSV-Export geschrieben.

Auf der **Startseite** öffnet ein Doppelklick auf eine Fehlzeit oder Verspätung direkt den WebUntis-Dialog. Bei mehreren bearbeitbaren Einträgen kannst du einen aus dem Menü auswählen. Ein Rechtsklick öffnet die Auswahl ebenfalls. Nach dem Speichern lädt die App die betroffene Klasse neu und aktualisiert die Tagesdetails. Mit **−** und **+** am rechten Rand der Überschrift klappst du die Matrix ein und aus.

## Datenschutzmodus

**Daten auf dem Gerät speichern** ist beim ersten Start deaktiviert. Nach der Aktivierung verschlüsselt die App vollständige Matrizen samt Tagesdetails und Abrufdatum mit AES-256-GCM. Beim Programmstart leitet sie den Schlüssel über einen gerätegebundenen Windows-Hello-Passkey ab. Die Funktion benötigt ein aktuelles Windows 11 ab 24H2 mit PRF-Unterstützung für Windows Hello.

Gespeicherte Matrizen erscheinen nach dem Entsperren mit Datum und Uhrzeit des letzten Abrufs. Die App aktualisiert den geöffneten Bereich im Hintergrund alle fünf Minuten, solange eine WebUntis-Sitzung verfügbar ist. Nach einem Abbruch lässt sich die Aktualisierung durch erneutes Öffnen der Matrix oder über **Aktualisieren** wieder aufnehmen. Beim Deaktivieren der Option oder beim Abmelden löscht die App die gespeicherten Matrizen.

Die [Kurzanleitung](README.txt) und die [Installations- und Buildhinweise](Start-hier.txt) liegen auch dem Setup bei.
