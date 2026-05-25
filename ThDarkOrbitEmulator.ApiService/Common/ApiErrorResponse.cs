namespace ThDarkOrbitEmulator.ApiService.Common;

public sealed class ApiErrorResponse
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public static ApiErrorResponse Create(string code, string? message = null)
        => new()
        {
            Code = code,
            Message = message ?? ApiErrorCatalog.GetMessage(code)
        };
}
