using WebUntisAuswertung.Core;

namespace WebUntisAuswertung.Mobile;

internal sealed partial class MatrixPage
{
    private readonly BrowserSurface refreshBrowser = new() { InputTransparent = true, Opacity = 0 };
    private readonly Dictionary<string, int> editRevisions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim matrixWrites = new(1);
    private AndroidReports? refreshReports;
    private CancellationTokenSource? deckRefresh;
    private TaskCompletionSource? deckRefreshFinished;
    private double deckProgress;
    private string? editingClassId;
    private bool changingSession;

    private async Task RefreshDeckAsync(bool force = false)
    {
        if (!sessionReady || locked || restoring || changingSession || changingSelection || Window == null ||
            deckRefresh != null || schoolYear is not { } year) return;
        var annual = year.ThroughToday();
        var pending = deck.Entries.Where(entry => entry.Class.Id != editingClassId && (force || NeedsRefresh(entry.Card.Matrix, annual)))
            .OrderBy(entry => entry != deck.Active).ToArray();
        if (pending.Length == 0) return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        deckRefresh = cancellation;
        deckRefreshFinished = completion;
        var token = cancellation.Token;
        deckProgress = 0;
        refreshStatus.Text = "Klassen werden aktualisiert …";
        UpdateControls();
        try
        {
            var previous = refreshBrowser.Browser;
            if (refreshBrowser.GetBrowser() is not { } view) return;
            if (!ReferenceEquals(view, previous)) refreshReports = null;
            var service = refreshReports ??= new AndroidReports(view, _ => { }, _ => { }, _ => Task.CompletedTask);
            var updated = 0;
            var incomplete = 0;
            var stored = true;
            var steps = AndroidReports.MatrixSteps;
            for (var index = 0; index < pending.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var (choice, card) = pending[index];
                var revision = editRevisions.GetValueOrDefault(choice.Id);
                if (choice.Id != editingClassId)
                {
                    if (card.Matrix == null) card.Clear(choice.Name, "Berichte werden geladen …");
                    try
                    {
                        var classProgress = new InlineProgress<string>(message =>
                            refreshStatus.Text = $"{index + 1}/{pending.Length} · {choice.Name} · {message}");
                        var data = await service.MatrixAsync(choice, annual, classProgress, token, allowSignIn: false,
                            reportProgress: (completed, _) => ShowProgress(index * steps + completed));
                        var matrix = await Task.Run(data.Build, token);
                        token.ThrowIfCancellationRequested();
                        // A report started before opening the editor may describe the old excuse status.
                        if (CanPublish())
                        {
                            if (matrix.HasCompleteReports)
                            {
                                if (!await RememberDeckMatrixAsync(service, choice, card, matrix, data, token, CanPublish)) stored = false;
                                if (CanPublish()) updated++;
                            }
                            else
                            {
                                if (card.Matrix is not { HasCompleteReports: true }) card.ShowMatrix(matrix);
                                incomplete++;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        ResetEmptyCard(card, choice, InitialUpdate);
                        throw;
                    }
                    catch (WebUntisSignInRequiredException)
                    {
                        ResetEmptyCard(card, choice, InitialUpdate);
                        throw;
                    }
                    catch (Exception error) when (!token.IsCancellationRequested &&
                        error is InvalidOperationException or TimeoutException or IOException or InvalidDataException or System.Text.Json.JsonException)
                    {
                        ResetEmptyCard(card, choice, "Daten konnten gerade nicht geladen werden.");
                        if (view.RendererGone) throw;
                        if (CanPublish()) incomplete++;
                    }
                }
                ShowProgress((index + 1) * steps);

                bool CanPublish() => !locked && !token.IsCancellationRequested &&
                    editingClassId != choice.Id && editRevisions.GetValueOrDefault(choice.Id) == revision;
            }
            ReportResult(!stored ? "Neue Daten geöffnet · Speichern fehlgeschlagen" :
                incomplete == 0 ? $"{updated} Klassen aktualisiert" : $"{updated} aktualisiert · {incomplete} Abrufe unvollständig");

            void ShowProgress(int completed)
            {
                deckProgress = (double)completed / (steps * pending.Length);
                UpdateFooter();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ReportResult("Aktualisierung abgebrochen");
        }
        catch (WebUntisSignInRequiredException)
        {
            if (!locked && !changingSession && !signInDismissed && !token.IsCancellationRequested)
            {
                if (transfer == null) sessionReady = false;
                signInRequired = true;
                ReportResult("Anmeldung erforderlich");
            }
        }
        catch (Exception error)
        {
            global::Android.Util.Log.Warn("FehlzeitenMX", $"Klassenabruf fehlgeschlagen ({error.GetType().Name}).");
            ReportResult(refreshBrowser.Browser is { RendererGone: true } ? MobileWebView.RendererMessage :
                "Aktualisierung momentan fehlgeschlagen");
        }
        finally
        {
            deckRefresh = null;
            deckRefreshFinished = null;
            try { if (!locked && Window != null) UpdateControls(); }
            finally { completion.TrySetResult(); }
        }
        if (signInRequired && !sessionReady) await CheckSessionAsync();

        void ResetEmptyCard(MatrixCard card, Student choice, string message)
        {
            if (!locked && !changingSelection && card.Matrix == null) card.Clear(choice.Name, message);
        }

        void ReportResult(string message)
        {
            if (!locked && !InteractiveTransferVisible) status.Text = message;
        }
    }

    private async Task StopDeckRefreshAsync()
    {
        var finished = deckRefreshFinished?.Task;
        deckRefresh?.Cancel();
        if (finished != null) await finished;
    }

    private static bool NeedsRefresh(AttendanceMatrix? matrix, ReportRange range) =>
        matrix is not { HasCompleteReports: true, FetchedAt: { } fetched } ||
        matrix.From != range.From || matrix.To != range.To || DateTimeOffset.UtcNow - fetched >= RefreshInterval;

    private async Task<bool> RememberDeckMatrixAsync(AndroidReports service, Student choice, MatrixCard card,
        AttendanceMatrix matrix, MatrixData data, CancellationToken token, Func<bool>? canPublish = null)
    {
        var hasAbsences = classSelection.Teachers.Contains(choice.Id) && matrix.Cells.Values.Any(HasExcusableEntries);
        if (hasAbsences && card.Matrix is { } previous && previous.From == matrix.From)
            matrix.PreparedAbsences = previous.PreparedAbsences;
        card.ShowMatrix(matrix);
        var stored = await SaveAsync();
        if (!hasAbsences || !CanSave()) return stored;
        var prepared = await service.PrepareAbsencesAsync(choice, data.Range, token);
        token.ThrowIfCancellationRequested();
        if (prepared == null || !CanSave()) return stored;
        matrix.PreparedAbsences = prepared;
        return await SaveAsync();

        bool CanSave() => ReferenceEquals(card.Matrix, matrix) && canPublish?.Invoke() != false;

        async Task<bool> SaveAsync()
        {
            await matrixWrites.WaitAsync(token);
            try
            {
                return !CanSave() || await TryCacheAsync(() =>
                    cache.SaveMatrixAsync(data with { PreparedAbsences = matrix.PreparedAbsences }, token));
            }
            finally { matrixWrites.Release(); }
        }
    }
}
