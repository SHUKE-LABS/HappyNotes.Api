using HappyNotes.Entities;

namespace HappyNotes.Services.interfaces;

public interface ISyncNoteService
{
    Task SyncNewNote(Note note, string fullContent);
    Task SyncEditNote(Note note, string fullContent, Note originalNote);
    Task SyncDeleteNote(Note note);
    Task SyncUndeleteNote(Note note);
    Task PurgeDeletedNotes(long userId);
}
