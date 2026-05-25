using Microsoft.AspNetCore.Authorization;

namespace ThDarkOrbitEmulator.ServiceDefaults.Authorization;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}
