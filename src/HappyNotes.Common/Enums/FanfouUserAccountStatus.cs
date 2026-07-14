namespace HappyNotes.Common.Enums;

public enum FanfouUserAccountStatus
{
    /// <summary>
    /// The initial status.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// Temporarily deactivated.
    /// </summary>
    Inactive = 2,

    /// <summary>
    /// Alias for disabled setting (Created, Tested, and Inactive).
    /// </summary>
    Disabled = Normal | Inactive,
}
