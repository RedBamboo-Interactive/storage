using System.Security.Claims;
using System.Text.Json;

namespace Leaf.Plugins.Storage;

internal static class StorageAccess
{
    public static void RequireAdministrator(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            throw new StorageAccessException(401);

        var admin = user.IsInRole("admin");
        foreach (var claim in user.FindAll("roles"))
        {
            if (claim.Value.Equals("admin", StringComparison.OrdinalIgnoreCase))
                admin = true;
            else if (claim.Value.StartsWith('['))
            {
                try
                {
                    admin |= JsonSerializer.Deserialize<string[]>(claim.Value)
                        ?.Contains("admin", StringComparer.OrdinalIgnoreCase) == true;
                }
                catch (JsonException)
                {
                    throw new StorageAccessException(403);
                }
            }
        }

        if (!admin) throw new StorageAccessException(403);
    }
}

internal sealed class StorageAccessException(int statusCode)
    : Exception("Storage inspection requires an authenticated installation administrator")
{
    public int StatusCode { get; } = statusCode;
}
