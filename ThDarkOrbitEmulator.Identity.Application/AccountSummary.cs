namespace ThDarkOrbitEmulator.Identity.Application;

public sealed record AccountSummary(Guid Id, string Username, string? Email, long GameUserId);
