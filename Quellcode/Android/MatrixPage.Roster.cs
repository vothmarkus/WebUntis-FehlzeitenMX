using Android.Widget;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung.Mobile;

internal sealed partial class MatrixPage
{
    private async Task ExportRosterAsync()
    {
        var selectedClasses = deck.Entries.Select(entry => entry.Class).ToArray();
        if (selectedClasses.Length == 0 || schoolYear is not { } year)
        {
            await NoticeAsync("Schüler-CSV", "Wähle unter Meine Klassen mindestens eine Klasse aus.");
            return;
        }

        var rows = new List<(string ClassName, Student Student)>();
        string? csv = null;
        var emailCount = 0;
        var startYear = SchoolYears.StartYear(year.From);
        var completed = await RunAsync(async (service, progress, token) =>
        {
            SetTransferProgress(0, 100);
            for (var index = 0; index < selectedClasses.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var choice = selectedClasses[index];
                var classProgress = new InlineProgress<string>(message =>
                    progress.Report($"{choice.Name} · {message}"));
                progress.Report($"{choice.Name} · Namensliste wird geladen …");
                var roster = await service.ReadClassRosterAsync(choice, year, classProgress, token);
                if (roster.Length == 0)
                    throw new InvalidOperationException($"WebUntis hat für «{choice.Name}» keine Namensliste geliefert.");
                rows.AddRange(roster.DistinctBy(student => student.Id).Select(student => (choice.Name, student)));
                SetTransferProgress((index + 1) * 35 / selectedClasses.Length, 100);
            }
            var contacts = await service.ReadLessonEmailsAsync(year, progress, (read, total) =>
            {
                if (total > 0) SetTransferProgress(35 + read * 60 / total, 100);
            }, token);
            token.ThrowIfCancellationRequested();
            (csv, emailCount) = await Task.Run(() => RosterCsv.Format(rows, startYear, contacts.Emails), token);
            token.ThrowIfCancellationRequested();
            SetTransferProgress(100, 100);
        });
        if (!completed || locked || csv == null || Platform.CurrentActivity is not MainActivity activity) return;

        var name = $"Schuelerliste-{SchoolYears.Label(startYear).Replace('/', '-')}.csv";
        try
        {
            status.Text = "Speicherort für die CSV auswählen …";
            if (await activity.SaveRosterCsvAsync(name, csv))
            {
                var missing = rows.Count - emailCount;
                var message = missing == 0
                    ? $"CSV gespeichert · {rows.Count} Zeilen mit E-Mail"
                    : $"CSV gespeichert · {emailCount} mit E-Mail · {missing} mit WebUntis-ID";
                if (!locked) status.Text = message;
                Toast.MakeText(activity, message, ToastLength.Long)?.Show();
            }
            else if (!locked) status.Text = "CSV-Speicherung abgebrochen";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Java.Lang.Exception)
        {
            const string message = "Die Schülerliste konnte nicht gespeichert werden.";
            if (!locked) status.Text = message;
            Toast.MakeText(activity, message, ToastLength.Long)?.Show();
        }
    }
}
