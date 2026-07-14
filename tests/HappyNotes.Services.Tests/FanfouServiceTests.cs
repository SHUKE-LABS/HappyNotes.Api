using HappyNotes.Common;
using HappyNotes.Services;

namespace HappyNotes.Services.Tests;

public class FanfouServiceTests
{
    [Test]
    public void PrepareStatusText_ShortContent_ReturnedUnchanged()
    {
        const string content = "a short note";
        var result = FanfouService.PrepareStatusText(content, noteId: 42);
        Assert.That(result, Is.EqualTo(content));
    }

    [Test]
    public void PrepareStatusText_AtLimit_ReturnedUnchanged()
    {
        var content = new string('x', Constants.FanfouStatusLength);
        var result = FanfouService.PrepareStatusText(content, noteId: 42);
        Assert.That(result, Is.EqualTo(content));
        Assert.That(result.Length, Is.EqualTo(Constants.FanfouStatusLength));
    }

    [Test]
    public void PrepareStatusText_LongContentWithNoteId_TruncatesWithLinkWithinBudget()
    {
        var content = new string('x', 500);
        var result = FanfouService.PrepareStatusText(content, noteId: 42);

        Assert.That(result.Length, Is.LessThanOrEqualTo(Constants.FanfouStatusLength),
            "status must never exceed Fanfou's 140-char limit");
        Assert.That(result, Does.Contain($"{Constants.HappyNotesWebsite}/note/42"),
            "over-length notes keep a permalink back to the note");
        Assert.That(result, Does.Contain("…"));
    }

    [Test]
    public void PrepareStatusText_LongContentWithoutNoteId_HardTruncates()
    {
        var content = new string('x', 500);
        var result = FanfouService.PrepareStatusText(content, noteId: 0);

        Assert.That(result.Length, Is.EqualTo(Constants.FanfouStatusLength));
        Assert.That(result, Does.Not.Contain("/note/"));
    }
}
