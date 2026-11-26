using WebUntisAuswertung.Core;
using Xunit;

namespace WebUntisAuswertung.Tests;

public sealed class AbsenceActionsTests
{
    private static readonly DateOnly Day = new(2026, 9, 7);

    [Fact]
    public void PreparedChoicesKeepOnlyTheConfirmedPersonAndDay()
    {
        AbsenceListEntry[] rows = [Row("second", "123", "09:00"), Row("different person", "456", "08:00"),
            Row("first", "123", "08:00"), Row("locked", "123", "07:30") with { CanExcuse = false },
            Row("different day", "123", "08:00") with { From = Day.AddDays(-1), To = Day.AddDays(-1) }];

        var found = AbsenceActions.ForDay(rows, new AbsencePerson("123", "Nora  Test"), Day);

        Assert.Equal(new[] { "first", "second" }, AbsenceActions.Available(Assert.IsType<AbsenceListEntry[]>(found))
            .Select(row => row.Id));
    }

    [Fact]
    public void PreparedRowsWithAmbiguousIdentityRequireALiveLookup()
    {
        AbsenceListEntry[] rows = [Row("ambiguous", "", "08:00"), Row("resolved", "123", "09:00")];
        Assert.Null(AbsenceActions.ForDay(rows, new AbsencePerson("123", "Nora Test"), Day));
    }

    [Fact]
    public void ConfirmationRequiresTheSavedStatusOnTheExactAbsence()
    {
        var edited = Row("first", "123", "08:00");
        var confirmed = edited with { Status = "Entschuldigt", CanExcuse = false };

        Assert.True(AbsenceActions.Confirms([confirmed], edited, Excuse.Excused));
        Assert.False(AbsenceActions.Confirms([edited, confirmed with { Id = "second" }], edited, Excuse.Excused));
        Assert.False(AbsenceActions.Confirms([confirmed with { StudentId = "456" }], edited, Excuse.Excused));
        Assert.False(AbsenceActions.Confirms([confirmed with { Start = "09:00" }], edited, Excuse.Excused));
        Assert.False(AbsenceActions.Confirms([confirmed with { Status = "Entschuldigung bearbeiten" }], edited, Excuse.Excused));
    }

    private static AbsenceListEntry Row(string id, string studentId, string start) =>
        new(id, studentId, "Nora Test", Day, Day, start, "10:00", "Unentschuldigt", "", true);
}
