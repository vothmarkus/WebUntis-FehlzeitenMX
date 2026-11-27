# WebUntis-FehlzeitenMX

Anwesenheitsmatrix für WebUntis am Berufskolleg für Technik Düren. Windows und die Android-Testversion verwenden dieselbe .NET-Bibliothek für Berichtssteuerung, CSV-Auswertung, Matrix und Tagesdetails. Die Windows-App exportiert auch Namenslisten für den Notenrechner.

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

## Android-Version 0.6.24

Installiere die APK auf einem Gerät mit Android 6 oder neuer und aktueller Android System WebView. Die APK ist für ARM64. Für einen x86-64-Emulator kann die App mit `-p:AndroidAbi=android-x64` gebaut werden.

Die reguläre App heißt **WebUntis-FehlzeitenMX**. Der Wechsel von 0.6.15 auf 0.6.17 erfordert wegen des neuen Signierschlüssels einmalig eine Neuinstallation. Dadurch musst du deine Anmeldung und Klassenauswahl einmal neu einrichten.

Unter **⋮ → Einstellungen** findest du **Anmeldung**, **Klassenauswahl**, **Legende**, **App-Update** und **Impressum**. **App-Update** sucht beim Öffnen der Einstellungen nach Android-Releases auf GitHub. Wenn eine neuere ARM64-Version verfügbar ist, lädt die App die APK auf Wunsch, prüft sie und öffnet die Android-Installation. Bestätige dort das Update. Android kann beim ersten In-App-Update nach der Installationsfreigabe für Fehlzeiten MX fragen.

Beim Start und beim Zurückkehren in die App entsperrst du sie über die Android-Abfrage mit Biometrie oder deiner Displaysperre.

Beim ersten Start erscheint nach dem Entsperren die Auswahl für die WebUntis-Anmeldung. Mit **Automatische Anmeldung** öffnest du direkt das Formular für deine Zugangsdaten. Nach **Speichern** verwendet die App diese für die erste Anmeldung. **Manuelle Anmeldung** öffnet direkt die WebUntis-Anmeldeseite. Mit **Später** kannst du die Anmeldung überspringen und sie in den Einstellungen einrichten.

**Bei WebUntis anmelden** öffnet die BKT-Anmeldeseite in der App und wählt auf der Begrüßungsseite automatisch **Im Browser öffnen**. Die App behält ihr WebUntis-Profil bei App-Sperre und Updates. Fehlende Sitzungscookies ergänzt sie aus der verschlüsselten Sicherung. Verlangt WebUntis eine neue Anmeldung, öffnet sich die Login-Seite aus der Matrix. Bei Bedarf kannst du über **Anmelden** in der Statuszeile erneut starten.

Unter **Einstellungen → Anmeldung → Automatische Anmeldung** kannst du deine Zugangsdaten für die direkte WebUntis-Anmeldung hinterlegen. Ab Android 8 kann der eingestellte Passwortmanager Benutzername und Passwort in diesem Formular ausfüllen. Nach dem Wechsel zum Passwortmanager führt dich das Entsperren zurück in das geöffnete Formular. Die App speichert die Zugangsdaten verschlüsselt auf diesem Gerät und erneuert eine abgelaufene Sitzung nach dem Entsperren. Zusätzliche Bestätigungen und externe Schulanmeldungen erledigst du auf der angezeigten Anmeldeseite. **Zugangsdaten löschen** und **Abmelden** entfernen die gespeicherten Zugangsdaten.

Die Klassenauswahl sortiert BKT-Kürzel mit gleicher Buchstabengruppe in der Stufenfolge **U, M, O, P, A**. Ein **P** am Ende folgt auf die **1** desselben Kürzels, etwa `ELO1`, `ELOP`, `ELO2`.

Unter **Meine Klassen** liegen die ausgewählten Matrizen in einem Stapel. Frühere Auswahlen für Startseite und Meine Klassen werden zusammengeführt. In der **Klassenauswahl** stehen **Meine Klassen** und **Klassenlehrer** nebeneinander. Setzt du **Klassenlehrer**, ist die Klasse automatisch ausgewählt und steht vor den anderen Klassen. Wische über den Klassennamen, in den seitlichen Freiflächen neben der Tabelle, unter ihr oder auf einer leeren Karte nach links zur nächsten Klasse und nach rechts zur vorherigen. Du kannst auch mit den Pfeilen wechseln. Ein Tipp auf den Klassennamen öffnet die Auswahlliste.

Die Matrizen öffnen sich bei den neuesten Unterrichtstagen. Wische seitlich für weitere Tage und vertikal für weitere Namen. Die Namensspalte und die Datumszeile bleiben sichtbar. Beim Scrollen zu weiteren Namen schiebt sich die App-Überschrift mit Logo und Menü nach oben. Beim Zurückscrollen fährt sie wieder ein.

Tippe für die Tagesdetails auf eine Zelle und oben rechts auf **×** zum Schließen. Zoome mit zwei Fingern. In einer als **Klassenlehrer** markierten Klasse öffnest du das Entschuldigungsformular durch langes Drücken auf die Zelle oder über den Stift oben rechts in den Tagesdetails. Gibt es mehrere bearbeitbare Abwesenheiten für diese Person und diesen Tag, kannst du den Eintrag auswählen. Die App hält verfügbare Einträge verschlüsselt vor und bereitet die WebUntis-Abwesenheitsliste für die aktive Klasse im Hintergrund vor. Sie öffnet den ausgewählten Eintrag aus dieser Liste, sobald er online bestätigt ist. Ist die Sitzung abgelaufen, zeigt die App die WebUntis-Anmeldung und öffnet anschließend den ausgewählten Eintrag. Das Formular passt sich bei jedem Öffnen automatisch an die Bildschirmbreite an. Zoome bei Bedarf mit zwei Fingern. Im Formular kannst du bis zu **Speichern** oder **Schließen** scrollen. Nach **Speichern** erscheint bei einer gewählten Entschuldigung zunächst `E*` in der Matrix. Du kannst weitere Abwesenheiten bearbeiten, während die App die Einträge und Berichte im Hintergrund abgleicht. Sobald der Abgleich abgeschlossen ist, zeigt die Matrix den bestätigten Tagesstand. Wenn WebUntis eine Änderung nicht bestätigt, meldet die App das in der Statuszeile.

**Meine Klassen** zeigt das Schuljahr bis zum jüngsten gespeicherten Abruf. In der **Tagesmatrix** kannst du über **Zeitraum** den Anfangs- und Endtag innerhalb dieses Schuljahres frei wählen. **Schuljahr bis heute** setzt den Zeitraum zurück.

Vollständige Matrizen der gewählten Klassen und die Klassenliste werden mit AES-256-GCM verschlüsselt auf dem Gerät gespeichert. Der Schlüssel liegt im Android Keystore. Beim Öffnen sind die gespeicherten Matrizen auch offline mit Datum und Uhrzeit des Abrufs verfügbar. In einer aktiven WebUntis-Sitzung aktualisiert die App ihre Klassen alle fünf Minuten im Hintergrund, auch während du Entschuldigungen bearbeitest. Nach dem Schließen des Entschuldigungsformulars landest du in der Matrix. Über **↻** kannst du sofort aktualisieren und dich bei Bedarf erneut anmelden. Die Statuszeile zeigt den Abruffortschritt. Ein unvollständiger Abruf lässt den letzten vollständigen Stand bestehen.

Über **⋮ → Schüler-CSV speichern …** exportierst du die vollständigen Schülerlisten aller ausgewählten Klassen. Die Datei hat dasselbe Format wie unter Windows. Bei fehlender E-Mail enthält die Zeile die WebUntis-ID. Den Speicherort wählst du im Android-Dateidialog.

**Abmelden** unter **Einstellungen → Anmeldung** löscht die lokal gespeicherte Sitzung, die Matrizen und die Klassenliste. Entschuldigungen sind in den Tagesdetails lesbar.

### APK bauen

Mit .NET 10, JDK 21 und der Android SDK Platform 36 samt Build-Tools:

```sh
dotnet workload install maui-android
dotnet build Quellcode/Android/WebUntisFehlzeitenMX.Android.csproj -c Release --no-incremental
```

Erzeuge jede Release-APK mit `--no-incremental` in einem frischen Build-Verzeichnis. Ein inkrementeller Build kann die nativen Android-Startmethoden auslassen und direkt beim Start abstürzen. Prüfe für ARM64, dass die `lib/arm64-v8a/libxamarin-app.so` aus der fertigen APK `MainActivity_n_1onCreate` als JNI-Methode exportiert. Unter Linux zeigt `readelf --dyn-syms --wide` die Exporte an.

Die APK liegt anschließend unter `Quellcode/Android/bin/Release/net10.0-android/android-arm64/`. Signiere die APK für die Veröffentlichung mit dem Release-Schlüssel und prüfe vor der Weitergabe mit `apksigner verify --print-certs` das Zertifikat. Bewahre den passwortgeschützten Keystore und das Passwort außerhalb des Repositorys an getrennten, dauerhaft verfügbaren Orten auf.

Das Release-Zertifikat ab 0.6.17 hat den SHA-256-Fingerabdruck `470684d861322a3830a22579fdbc9f979c70191cec0168085e1abc05f3ae67d8`.

Android-Updates werden als GitHub-Prerelease mit einem Tag wie `android-v0.6.5` und der signierten Datei `WebUntis-FehlzeitenMX-Android-0.6.5-arm64.apk` veröffentlicht. Setze den Tag auf den Commit, zu dem die APK gebaut wurde, und erhöhe bei jedem Update `ApplicationVersion` und `ApplicationDisplayVersion`. Windows verwendet weiterhin die stabilen Releases mit `v…`-Tag.

Die gemeinsame CSV-Auswertung lässt sich mit fiktiven Berichten prüfen:

```sh
dotnet test Quellcode/Tests/WebUntisAuswertung.Tests.csproj
```
