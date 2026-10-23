using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Policies;

namespace SyncDrive.Server.Host;

/// <summary>Admin-Konsole (M7): Benutzerverwaltung. Nur Administratoren; die Prüfung steckt im <see cref="AdminService"/>.</summary>
public static partial class ApiEndpoints
{
    private static void MapAdmin(RouteGroupBuilder secured)
    {
        secured.MapGet("/admin/users", async (HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.ListUsersAsync(Caller(http), ct)));
        secured.MapPost("/admin/users", async (CreateUserRequest request, HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.CreateUserAsync(Caller(http), request, ct)));
        secured.MapPatch("/admin/users/{userId}", async (string userId, UpdateUserRequest request, HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.UpdateUserAsync(Caller(http), userId, request, ct)));
        secured.MapPut("/admin/users/{userId}/password", async (string userId, SetPasswordRequest request, HttpContext http, AdminService admin, CancellationToken ct) =>
            await admin.SetPasswordAsync(Caller(http), userId, request, ct) is { } error ? Problem(error) : Results.NoContent());

        secured.MapGet("/policies", async (PolicyService policies, CancellationToken ct) => Results.Ok(await policies.GetAsync(ct)));
        secured.MapPut("/admin/policies", async (SharingPolicies request, HttpContext http, PolicyService policies, CancellationToken ct) =>
            Respond(await policies.UpdateAsync(Caller(http), request, ct)));

        secured.MapGet("/admin/groups", async (HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.ListGroupsAsync(Caller(http), ct)));
        secured.MapPost("/admin/groups", async (NameRequest request, HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.CreateGroupAsync(Caller(http), request, ct)));
        secured.MapDelete("/admin/groups/{groupId}", async (string groupId, HttpContext http, AdminService admin, CancellationToken ct) =>
            await admin.DeleteGroupAsync(Caller(http), groupId, ct) is { } error ? Problem(error) : Results.NoContent());
        secured.MapPut("/admin/groups/{groupId}/members/{userId}", async (string groupId, string userId, HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.SetGroupMemberAsync(Caller(http), groupId, userId, member: true, ct)));
        secured.MapDelete("/admin/groups/{groupId}/members/{userId}", async (string groupId, string userId, HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.SetGroupMemberAsync(Caller(http), groupId, userId, member: false, ct)));

        secured.MapGet("/admin/team-folders", async (HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.ListTeamFoldersAsync(Caller(http), ct)));
        secured.MapPost("/admin/team-folders", async (NameRequest request, HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.CreateTeamFolderAsync(Caller(http), request, ct)));
        secured.MapPatch("/admin/team-folders/{spaceId}", async (string spaceId, NameRequest request, HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.RenameTeamFolderAsync(Caller(http), spaceId, request, ct)));
        secured.MapDelete("/admin/team-folders/{spaceId}", async (string spaceId, HttpContext http, AdminService admin, CancellationToken ct) =>
            await admin.DeleteTeamFolderAsync(Caller(http), spaceId, ct) is { } error ? Problem(error) : Results.NoContent());
        secured.MapPut("/admin/team-folders/{spaceId}/members", async (string spaceId, TeamMemberRequest request, HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.SetTeamMemberAsync(Caller(http), spaceId, request, ct)));
        secured.MapDelete("/admin/team-folders/{spaceId}/members/{kind}/{memberId}", async (
            string spaceId, string kind, string memberId, HttpContext http, AdminService admin, CancellationToken ct) =>
            Respond(await admin.RemoveTeamMemberAsync(Caller(http), spaceId, kind, memberId, ct)));
    }
}
