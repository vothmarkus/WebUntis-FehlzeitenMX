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

## Android-Testversion 0.4.0

Installiere die APK auf einem Gerät mit Android 6 oder neuer und aktueller Android System WebView. Die Test-APK ist für ARM64. Für einen x86-64-Emulator kann die App mit `-p:AndroidAbi=android-x64` gebaut werden.

Deinstalliere beim Wechsel von 0.3.0 oder älter einmalig die alte Testversion und wähle die Klassen nach der Installation erneut aus. Ab 0.4.0 verwendet die APK einen eigenen Signierschlüssel.

**Bei WebUntis anmelden** öffnet die BKT-Anmeldeseite in der App und wählt auf der Begrüßungsseite automatisch **Im Browser öffnen**. Melde dich mit deinem Schulkonto an und tippe danach bei Bedarf auf **Weiter**, um die Klassenliste zu laden.

Über **⋮** wechselst du zwischen **Startseite**, **Meine Klassen** und **Tagesmatrix**. Unter **Klassen auswählen** legst du die beiden Klassenlisten getrennt fest. Auf der Startseite und unter **Meine Klassen** liegt jeweils eine Klassenmatrix oben auf dem Stapel. Wische über den Klassennamen nach links oder rechts oder tippe auf die Pfeile, um die Klasse zu wechseln. Ein Tipp auf den Klassennamen öffnet die Auswahlliste. Über **WebUntis-Konto** kannst du die Klassenliste aktualisieren und dich abmelden.

Die Matrizen öffnen sich bei den neuesten Unterrichtstagen. Wische seitlich für weitere Tage und vertikal für weitere Namen. Die Namensspalte und die Datumszeile bleiben sichtbar. Beim Scrollen zu weiteren Namen klappt die App-Überschrift ein. Beim Zurückscrollen erscheint sie wieder. Das Dreipunktmenü bleibt über dem Tabellenbereich erreichbar.

Tippe für die Tagesdetails auf eine Zelle. Zoome mit zwei Fingern oder über die Prozent-Schaltfläche in der Matrix.

Die Startseite und **Meine Klassen** zeigen das aktuelle Schuljahr bis heute. In der **Tagesmatrix** kannst du über **Zeitraum** den Anfangs- und Endtag innerhalb dieses Schuljahres frei wählen. Die Auswahl gilt auch beim Klassenwechsel. **Schuljahr bis heute** setzt den Zeitraum zurück.

Die Testversion hält die Matrix im Arbeitsspeicher und startet jede App-Sitzung mit einem bereinigten WebView-Profil. Entschuldigungen lassen sich in den Tagesdetails lesen. Die Bearbeitung, ein geschützter Offline-Speicher und das Merken der Anmeldung sind für einen späteren Stand vorgesehen.

### APK bauen

Mit .NET 10, JDK 21 und der Android SDK Platform 36 samt Build-Tools:

```sh
dotnet workload install maui-android
dotnet build Quellcode/Android/WebUntisFehlzeitenMX.Android.csproj -c Release
```

Die mit dem lokalen Testschlüssel signierte APK liegt anschließend unter `Quellcode/Android/bin/Release/net10.0-android/android-arm64/`. Für die Weitergabe von Updates müssen alle APKs mit demselben Schlüssel signiert sein.

Die gemeinsame CSV-Auswertung lässt sich mit fiktiven Berichten prüfen:

```sh
dotnet test Quellcode/Tests/WebUntisAuswertung.Tests.csproj
```
