using System.Text.RegularExpressions;
using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Sharing;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Items;

/// <summary>
/// Favoriten und Labels (B8). Gemerkt wird die Knoten-Id, angezeigt der aktuelle Pfad: Nach Umbenennen oder Verschieben bleiben Favoriten
/// und Labels erhalten; gelöschte oder nicht mehr zugängliche Elemente erscheinen nicht.
/// </summary>
public sealed partial class ItemService(IUserItemStore items, SharingService sharing)
{
    private const int MaxLabelName = 64;
    private const string DefaultColor = "#7f8c8d";

    public async Task<ServiceError?> SetStarAsync(Principal caller, string spaceId, string path, bool starred, CancellationToken ct)
    {
        if (await ResolveAsync(caller, spaceId, path, ct) is not { } node)
        {
            return new ServiceError(ErrorCode.NotFound, "Element existiert nicht.");
        }

        await items.SetStarAsync(caller.UserId, node, starred, ct);
        return null;
    }

    public async Task<ItemsResponse> ListStarsAsync(Principal caller, CancellationToken ct) =>
        new(await VisibleAsync(caller, await items.ListStarsAsync(caller.UserId, ct), ct));

    public async Task<ServiceResult<LabelInfo>> CreateLabelAsync(Principal caller, CreateLabelRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxLabelName)
        {
            return ServiceResult.Fail<LabelInfo>(new ServiceError(ErrorCode.InvalidRequest, "Labelname ungültig."));
        }

        var color = request.Color is { } c && ColorPattern().IsMatch(c) ? c.ToLowerInvariant() : DefaultColor;
        var label = new LabelRecord("l_" + Guid.NewGuid().ToString("N"), caller.UserId, name, color);
        await items.SaveLabelAsync(label, ct);
        return ServiceResult.Ok(new LabelInfo(label.Id, label.Name, label.Color));
    }

    public async Task<LabelsResponse> ListLabelsAsync(Principal caller, CancellationToken ct) =>
        new([.. (await items.ListLabelsAsync(caller.UserId, ct)).Select(l => new LabelInfo(l.Id, l.Name, l.Color))]);

    public Task<bool> DeleteLabelAsync(Principal caller, string labelId, CancellationToken ct) =>
        items.DeleteLabelAsync(caller.UserId, labelId, ct);

    public async Task<ServiceError?> SetLabelAsync(
        Principal caller, string labelId, string spaceId, string path, bool assigned, CancellationToken ct)
    {
        if (!await OwnsLabelAsync(caller, labelId, ct))
        {
            return new ServiceError(ErrorCode.NotFound, "Label unbekannt.");
        }

        if (await ResolveAsync(caller, spaceId, path, ct) is not { } node)
        {
            return new ServiceError(ErrorCode.NotFound, "Element existiert nicht.");
        }

        await items.SetLabelAsync(labelId, node, assigned, ct);
        return null;
    }

    public async Task<ServiceResult<ItemsResponse>> ListLabeledAsync(Principal caller, string labelId, CancellationToken ct)
    {
        if (!await OwnsLabelAsync(caller, labelId, ct))
        {
            return ServiceResult.Fail<ItemsResponse>(new ServiceError(ErrorCode.NotFound, "Label unbekannt."));
        }

        return ServiceResult.Ok(new ItemsResponse(await VisibleAsync(caller, await items.ListLabeledAsync(labelId, ct), ct)));
    }

    private async Task<bool> OwnsLabelAsync(Principal caller, string labelId, CancellationToken ct) =>
        (await items.ListLabelsAsync(caller.UserId, ct)).Any(l => l.Id == labelId);

    /// <summary>Über den View, mit dem der Aufrufer den Space kennt: auch Elemente in geteilten Ordnern lassen sich markieren.</summary>
    private async Task<NodeReference?> ResolveAsync(Principal caller, string spaceId, string path, CancellationToken ct)
    {
        if (await sharing.OpenAsync(caller, spaceId, SpaceRole.Previewer, ct) is not { IsSuccess: true } view)
        {
            return null;
        }

        var node = await view.Value!.GetNodeAsync(path, ct);
        return node is { IsSuccess: true, Value.Node.Deleted: false } ? new NodeReference(spaceId, node.Value.Node.NodeId) : null;
    }

    private async Task<List<ItemRef>> VisibleAsync(Principal caller, IReadOnlyList<NodeReference> references, CancellationToken ct)
    {
        var visible = new List<ItemRef>();
        foreach (var reference in references)
        {
            if (await sharing.OpenAsync(caller, reference.SpaceId, SpaceRole.Previewer, ct) is not { IsSuccess: true } view)
            {
                continue; // Zugriff entzogen
            }

            if (await view.Value!.GetNodeByIdAsync(reference.NodeId, ct) is { IsSuccess: true } node)
            {
                var info = node.Value!;
                visible.Add(new ItemRef(reference.SpaceId, reference.NodeId, info.Path, info.Kind, info.Size, info.MTime, info.Revision));
            }
        }

        return visible;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();
}
