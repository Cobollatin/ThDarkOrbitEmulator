namespace ThDarkOrbitEmulator.Identity.Application.Backoffice;

public sealed record BackofficeAccountRow(
    Guid Id,
    string Username,
    string Email,
    long GameUserId);
