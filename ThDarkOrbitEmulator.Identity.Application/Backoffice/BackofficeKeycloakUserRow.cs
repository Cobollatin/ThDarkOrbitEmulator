namespace ThDarkOrbitEmulator.Identity.Application.Backoffice;

public sealed record BackofficeKeycloakUserRow(
    string Id,
    string Username,
    string Email,
    bool Enabled);
