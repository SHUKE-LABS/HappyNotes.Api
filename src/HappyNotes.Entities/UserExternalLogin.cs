using SqlSugar;

namespace HappyNotes.Entities;

public class UserExternalLogin
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderSubject { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
}
