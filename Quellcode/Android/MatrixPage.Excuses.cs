using WebUntisAuswertung.Core;

namespace WebUntisAuswertung.Mobile;

internal sealed partial class MatrixPage
{
    private readonly Dictionary<(string ClassId, string AbsenceId), PendingExcuse> pendingExcuses = [];
    private bool checkingExcuses;
    private string? excuseSyncIssue;

    private sealed record PendingExcuse(Student Class, string StudentId, DateOnly Day, AbsenceListEntry Row, Excuse Status);

    private void QueueExcuse(AttendanceMatrix matrix, Student student, DateOnly day, AbsenceListEntry row, Excuse status)
    {
        var choice = new Student(matrix.ClassId, matrix.ClassName);
        pendingExcuses[(choice.Id, row.Id)] = new PendingExcuse(choice, student.Id, day, row, status);
        excuseSyncIssue = null;
        matrix.PreparedAbsences = null;
        foreach (var card in CardsForClass(choice.Id))
        {
            if (card.Matrix is { } current) current.PreparedAbsences = null;
            card.RedrawCells();
        }
        UpdateFooter();
    }

    private bool IsExcusePending(string classId, string studentId, DateOnly day) => pendingExcuses.Values.Any(edit =>
        edit.Class.Id == classId && edit.StudentId == studentId && edit.Status == Excuse.Excused &&
        edit.Row.From <= day && day <= edit.Row.To);

    private IEnumerable<MatrixCard> CardsForClass(string classId)
    {
        if (matrixCard.Matrix?.ClassId == classId) yield return matrixCard;
        foreach (var entry in deck.Entries)
            if (entry.Class.Id == classId) yield return entry.Card;
    }

    private async Task RefreshExcusesAsync()
    {
        if (!CanStart()) return;
        if (deckRefresh != null)
        {
            await StopDeckRefreshAsync();
            if (!CanStart() || deckRefresh != null) return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        deckRefresh = cancellation;
        deckRefreshFinished = completion;
        checkingExcuses = true;
        deckProgress = 0;
        refreshStatus.Text = "Entschuldigungen werden mit WebUntis abgeglichen …";
        UpdateControls();
        var token = cancellation.Token;
        var unconfirmed = 0;
        var cacheFailed = false;
        try
        {
            var service = GetRefreshReports() ?? throw new InvalidOperationException(MobileWebView.RendererMessage);
            while (pendingExcuses.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var choice = pendingExcuses.Values.First().Class;
                var batch = pendingExcuses.Values.Where(edit => edit.Class.Id == choice.Id).ToArray();
                var days = new ReportRange(batch.Min(edit => edit.Day), batch.Max(edit => edit.Day));
                var annual = schoolYear!.ThroughToday();
                var daily = matrixCard.Matrix?.ClassId == choice.Id ? matrixCard.Matrix : null;
                var reportRange = annual with { To = new[] { annual.To, days.To, daily?.To ?? annual.To }.Max() };
                refreshStatus.Text = $"{choice.Name} · Entschuldigungen werden abgeglichen …";
                var batchUnconfirmed = batch.Length;
                try
                {
                    var rows = await service.PrepareAbsencesAsync(choice, days, token, requireFresh: true);
                    if (batch.Any(edit => !Confirmed(edit, rows)))
                    {
                        await Task.Delay(1200, token);
                        rows = await service.PrepareAbsencesAsync(choice, days, token, requireFresh: true);
                    }
                    var progress = new InlineProgress<string>(message => refreshStatus.Text = $"{choice.Name} · {message}");
                    var data = await service.MatrixAsync(choice, reportRange, progress, token, allowSignIn: false,
                        afterExcuse: true, reportProgress: (completed, total) =>
                        {
                            deckProgress = (double)completed / total;
                            UpdateFooter();
                        });
                    var annualData = data with { Range = annual };
                    var matrix = await Task.Run(annualData.Build, token);
                    token.ThrowIfCancellationRequested();
                    if (matrix.HasCompleteReports)
                    {
                        var deckCard = deck.Entries.FirstOrDefault(entry => entry.Class.Id == choice.Id)?.Card;
                        if (deckCard != null && !await RememberDeckMatrixAsync(service, choice, deckCard, matrix, annualData,
                            token, prepareAbsences: false)) cacheFailed = true;
                        if (matrixCard.Matrix is { } visible && visible.ClassId == choice.Id &&
                            reportRange.Contains(visible.From) && reportRange.Contains(visible.To))
                        {
                            var dailyData = data with { Range = new ReportRange(visible.From, visible.To) };
                            var updated = dailyData.Range == annual ? matrix : await Task.Run(dailyData.Build, token);
                            token.ThrowIfCancellationRequested();
                            if (ReferenceEquals(matrixCard.Matrix, visible)) matrixCard.ShowMatrix(updated);
                        }
                        batchUnconfirmed = batch.Count(edit => !Confirmed(edit, rows));
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException or
                    InvalidDataException or System.Text.Json.JsonException)
                {
                    if (refreshBrowser.Browser is { RendererGone: true }) throw;
                }
                token.ThrowIfCancellationRequested();
                unconfirmed += batchUnconfirmed;
                foreach (var edit in batch) pendingExcuses.Remove((choice.Id, edit.Row.Id));
                foreach (var card in CardsForClass(choice.Id)) card.RedrawCells();
                deckProgress = 0;
            }
            excuseSyncIssue = unconfirmed > 0 ? $"WebUntis hat {unconfirmed} Änderung(en) nicht bestätigt · erneut aktualisieren" :
                cacheFailed ? "WebUntis abgeglichen · Stand konnte lokal nicht gespeichert werden" : null;
            if (!InteractiveTransferVisible) status.Text = "Entschuldigungen mit WebUntis abgeglichen";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (WebUntisSignInRequiredException)
        {
            if (!locked && !changingSession && !token.IsCancellationRequested)
            {
                if (transfer == null) sessionReady = false;
                signInRequired = true;
                excuseSyncIssue = "Anmeldung für den Abgleich erforderlich";
            }
        }
        catch (Exception error)
        {
            global::Android.Util.Log.Warn("FehlzeitenMX", $"Entschuldigungsabgleich fehlgeschlagen ({error.GetType().Name}).");
            excuseSyncIssue = "Abgleich fehlgeschlagen · erneut aktualisieren";
        }
        finally
        {
            checkingExcuses = false;
            deckRefresh = null;
            deckRefreshFinished = null;
            try { if (!locked && Window != null) UpdateControls(); }
            finally { completion.TrySetResult(); }
        }

        bool CanStart() => pendingExcuses.Count > 0 && sessionReady && !checkingExcuses && !locked && !restoring &&
            !changingSession && !changingSelection && schoolYear != null && Window != null;

        static bool Confirmed(PendingExcuse edit, AbsenceListEntry[]? rows) => edit.Status == Excuse.Unspecified ||
            rows != null && AbsenceActions.Confirms(rows, edit.Row, edit.Status);
    }
}
