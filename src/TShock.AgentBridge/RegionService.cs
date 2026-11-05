using System.Text.Json.Serialization;
using Terraria;

namespace TShock.AgentBridge;

internal sealed record RegionDto(
    int Id,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    string Owner,
    bool DisableBuild,
    int Z,
    int[] AllowedIds,
    string[] AllowedGroups);

internal sealed record RegionCreateRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("owner")] string? Owner);

/// <summary>
/// Region operations. All of them run on the game thread through the dispatcher,
/// which keeps reads consistent and mutations serialized with the world tick.
/// </summary>
internal sealed class RegionService
{
    private readonly MainThreadDispatcher _dispatcher;
    private readonly RegionOwnerPolicy _owners;

    public RegionService(MainThreadDispatcher dispatcher, RegionOwnerPolicy owners)
    {
        _dispatcher = dispatcher;
        _owners = owners;
    }

    public Task<RegionDto[]> ListAsync() =>
        _dispatcher.InvokeAsync(() =>
        {
            var worldId = Main.worldID.ToString();
            // RegionManager.ListAllRegions only selects RegionName and returns stub objects,
            // so read the in-memory list, which carries the full record.
            return TShockAPI.TShock.Regions.Regions
                .Where(region => region.WorldID == worldId)
                .Select(ToDto)
                .ToArray();
        });

    public Task<RegionDto?> GetAsync(string name) =>
        _dispatcher.InvokeAsync(() =>
        {
            var region = TShockAPI.TShock.Regions.GetRegionByName(name);
            return region is null ? null : ToDto(region);
        });

    public async Task<(RegionDto? Region, string? Error)> CreateAsync(RegionCreateRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return (null, WireCodes.NameRequired);
        }

        if (request.Width <= 0 || request.Height <= 0)
        {
            return (null, WireCodes.InvalidSize);
        }

        if (request.X < 0 || request.Y < 0)
        {
            return (null, WireCodes.InvalidPosition);
        }

        // The admin decides who may own a region: the bridge never invents an
        // owner that the server has not explicitly allowed.
        var owner = string.IsNullOrWhiteSpace(request.Owner) ? _owners.DefaultOwner : request.Owner.Trim();
        if (!_owners.IsAllowed(owner))
        {
            return (null, WireCodes.OwnerNotAllowed);
        }

        return await _dispatcher.InvokeAsync(() =>
        {
            // Bounds are only known on the game thread.
            if (request.X + request.Width > Main.maxTilesX || request.Y + request.Height > Main.maxTilesY)
            {
                return ((RegionDto?)null, (string?)WireCodes.OutOfBounds);
            }

            if (TShockAPI.TShock.Regions.GetRegionByName(name) is not null)
            {
                return (null, WireCodes.RegionExists);
            }

            var worldId = Main.worldID.ToString();
            if (!TShockAPI.TShock.Regions.AddRegion(
                    request.X, request.Y, request.Width, request.Height, name, owner, worldId))
            {
                return (null, WireCodes.CreateFailed);
            }

            var created = TShockAPI.TShock.Regions.GetRegionByName(name);
            return (created is null ? null : ToDto(created), (string?)null);
        }).ConfigureAwait(false);
    }

    public Task<(bool Deleted, string? Error)> DeleteAsync(string name) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return (false, (string?)WireCodes.NameRequired);
            }

            if (TShockAPI.TShock.Regions.GetRegionByName(name) is null)
            {
                return (false, (string?)WireCodes.RegionNotFound);
            }

            return TShockAPI.TShock.Regions.DeleteRegion(name)
                ? (true, (string?)null)
                : (false, (string?)WireCodes.DeleteFailed);
        });

    private static RegionDto ToDto(TShockAPI.DB.Region region) =>
        new(
            region.ID,
            region.Name,
            region.Area.X,
            region.Area.Y,
            region.Area.Width,
            region.Area.Height,
            region.Owner,
            region.DisableBuild,
            region.Z,
            region.AllowedIDs.ToArray(),
            region.AllowedGroups.ToArray());
}
